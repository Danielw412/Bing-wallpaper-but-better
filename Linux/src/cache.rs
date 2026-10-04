//! On-disk cache of downloaded wallpapers and the metadata describing them.
//!
//! Layout under the data directory (`~/.local/share/daily-wallpaper/`):
//!
//! ```text
//! metadata.json   cached wallpapers (oldest first) and the one applied
//! lock            flock()ed while state is being changed
//! images/         the wallpapers themselves, plus in-progress downloads
//! ```

use std::fs::{self, File, OpenOptions, TryLockError};
use std::io::{ErrorKind, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::error::Error;

const METADATA_FILE: &str = "metadata.json";
const LOCK_FILE: &str = "lock";
const IMAGES_DIR: &str = "images";
const DOWNLOAD_PREFIX: &str = ".download-";
const DOWNLOAD_SUFFIX: &str = ".tmp";
/// Downloads older than this were abandoned by a killed process.
const STALE_DOWNLOAD_AGE: Duration = Duration::from_secs(60 * 60);
/// The lock is only held for local file and gsettings operations, so waiting
/// longer than this means something is wrong.
const LOCK_TIMEOUT: Duration = Duration::from_secs(15);

/// Persistent record of the cache.
#[derive(Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct State {
    /// Filename of the wallpaper most recently applied by this program.
    #[serde(default)]
    pub current: Option<String>,
    /// Cached wallpapers, oldest first.
    #[serde(default)]
    pub wallpapers: Vec<Entry>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Entry {
    /// Publication date, YYYY-MM-DD.
    pub date: String,
    /// File name inside the images directory.
    pub filename: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub copyright: Option<String>,
    /// Provider name, e.g. "bing".
    pub source: String,
    /// Provider's identifier for the image.
    pub id: String,
    /// URL the image was downloaded from.
    pub url: String,
}

impl Entry {
    /// Short human-readable description, e.g. "2026-10-03 – Catch, eat, repeat".
    pub fn label(&self) -> String {
        match &self.title {
            Some(title) => format!("{} – {title}", self.date),
            None => format!("{} – {}", self.date, self.filename),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    Previous,
    Next,
}

impl State {
    pub fn find(&self, source: &str, id: &str) -> Option<&Entry> {
        self.wallpapers
            .iter()
            .find(|e| e.source == source && e.id == id)
    }

    pub fn current_index(&self) -> Option<usize> {
        let current = self.current.as_deref()?;
        self.wallpapers.iter().position(|e| e.filename == current)
    }

    /// Add (or replace) an entry, keeping the list sorted oldest first.
    pub fn insert(&mut self, entry: Entry) {
        self.wallpapers.retain(|e| {
            e.filename != entry.filename && !(e.source == entry.source && e.id == entry.id)
        });
        self.wallpapers.push(entry);
        self.sort();
    }

    /// Sort by date. The sort is stable, so wallpapers sharing a date (e.g.
    /// after switching market) stay in download order.
    fn sort(&mut self) {
        self.wallpapers.sort_by(|a, b| a.date.cmp(&b.date));
    }

    /// Index of the wallpaper before/after the current one, wrapping around.
    /// If the current wallpaper is unknown, the newest is treated as current.
    pub fn neighbour(&self, direction: Direction) -> Option<usize> {
        let len = self.wallpapers.len();
        if len == 0 {
            return None;
        }
        let current = self.current_index().unwrap_or(len - 1);
        Some(match direction {
            Direction::Previous => (current + len - 1) % len,
            Direction::Next => (current + 1) % len,
        })
    }

    /// Drop the oldest entries until at most `keep` remain, never dropping the
    /// current wallpaper. `Cache::sweep` deletes the files afterwards.
    pub fn prune(&mut self, keep: usize) {
        let mut i = 0;
        while self.wallpapers.len() > keep && i < self.wallpapers.len() {
            if self.current.as_deref() == Some(self.wallpapers[i].filename.as_str()) {
                i += 1;
            } else {
                self.wallpapers.remove(i);
            }
        }
    }
}

pub struct Cache {
    dir: PathBuf,
}

/// Held while changing the cache; released when dropped.
pub struct Lock {
    _file: File,
}

impl Cache {
    pub fn new(dir: PathBuf) -> Self {
        Cache { dir }
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    fn images_dir(&self) -> PathBuf {
        self.dir.join(IMAGES_DIR)
    }

    fn metadata_path(&self) -> PathBuf {
        self.dir.join(METADATA_FILE)
    }

    pub fn image_path(&self, filename: &str) -> PathBuf {
        self.images_dir().join(filename)
    }

    /// Take the exclusive cache lock, waiting briefly if another instance
    /// holds it.
    pub fn lock(&self) -> Result<Lock, Error> {
        create_dir(&self.dir)?;
        let path = self.dir.join(LOCK_FILE);
        let file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(&path)
            .map_err(|e| Error::cache(format!("cannot open {}", path.display()), e))?;
        let deadline = Instant::now() + LOCK_TIMEOUT;
        loop {
            match file.try_lock() {
                Ok(()) => return Ok(Lock { _file: file }),
                Err(TryLockError::WouldBlock) if Instant::now() < deadline => {
                    thread::sleep(Duration::from_millis(100));
                }
                Err(TryLockError::WouldBlock) => return Err(Error::Locked),
                Err(TryLockError::Error(e)) => {
                    return Err(Error::cache(format!("cannot lock {}", path.display()), e));
                }
            }
        }
    }

    /// Read the metadata, ignoring entries whose image file has gone missing.
    /// Safe without the lock because the metadata is replaced atomically.
    pub fn load(&self) -> Result<State, Error> {
        let path = self.metadata_path();
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(e) if e.kind() == ErrorKind::NotFound => return Ok(State::default()),
            Err(e) => return Err(Error::cache(format!("cannot read {}", path.display()), e)),
        };
        let mut state: State = serde_json::from_str(&text).map_err(|e| {
            Error::Cache(format!(
                "{} is corrupt ({e}); delete it to start afresh",
                path.display()
            ))
        })?;
        state
            .wallpapers
            .retain(|e| is_plain_filename(&e.filename) && self.image_path(&e.filename).is_file());
        state.sort();
        Ok(state)
    }

    /// Atomically replace the metadata. Call with the lock held.
    pub fn save(&self, state: &State) -> Result<(), Error> {
        create_dir(&self.dir)?;
        let path = self.metadata_path();
        let tmp = self.dir.join(format!("{METADATA_FILE}.tmp"));
        let json = serde_json::to_string_pretty(state).expect("state serialises to JSON");
        let write = || -> std::io::Result<()> {
            let mut file = File::create(&tmp)?;
            file.write_all(json.as_bytes())?;
            file.write_all(b"\n")?;
            file.sync_all()?;
            fs::rename(&tmp, &path)
        };
        write().map_err(|e| {
            let _ = fs::remove_file(&tmp);
            Error::cache(format!("cannot write {}", path.display()), e)
        })
    }

    /// Create a uniquely named temporary file for a download. It lives next to
    /// the final images so it can be renamed into place atomically.
    pub fn start_download(&self) -> Result<Download, Error> {
        let dir = self.images_dir();
        create_dir(&dir)?;
        let path = dir.join(format!(
            "{DOWNLOAD_PREFIX}{}{DOWNLOAD_SUFFIX}",
            std::process::id()
        ));
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(&path)
            .map_err(|e| Error::cache(format!("cannot create {}", path.display()), e))?;
        Ok(Download {
            path,
            file,
            persisted: false,
        })
    }

    /// Delete image files that `state` does not list: pruned wallpapers and
    /// leftovers from runs that failed to apply theirs. Also delete downloads
    /// abandoned by killed processes. Failures are reported but not fatal.
    ///
    /// Call with the lock held, after `state` has been saved and its current
    /// wallpaper applied, so nothing deleted can still be on screen.
    pub fn sweep(&self, state: &State) {
        let Ok(dir) = fs::read_dir(self.images_dir()) else {
            return;
        };
        for item in dir.flatten() {
            let Ok(metadata) = item.metadata() else {
                continue;
            };
            if !metadata.is_file() {
                continue;
            }
            let name = item.file_name();
            let name = name.to_string_lossy();
            let remove = if name.starts_with(DOWNLOAD_PREFIX) && name.ends_with(DOWNLOAD_SUFFIX) {
                metadata
                    .modified()
                    .is_ok_and(|t| t.elapsed().unwrap_or_default() > STALE_DOWNLOAD_AGE)
            } else {
                !state.wallpapers.iter().any(|e| e.filename == name)
            };
            if remove
                && let Err(e) = fs::remove_file(item.path())
                && e.kind() != ErrorKind::NotFound
            {
                eprintln!(
                    "daily-wallpaper: cannot delete {}: {e}",
                    item.path().display()
                );
            }
        }
    }
}

fn create_dir(dir: &Path) -> Result<(), Error> {
    fs::create_dir_all(dir).map_err(|e| Error::cache(format!("cannot create {}", dir.display()), e))
}

/// Guards against metadata entries escaping the images directory.
fn is_plain_filename(name: &str) -> bool {
    !name.is_empty() && Path::new(name).file_name().is_some_and(|f| f == name)
}

/// File name for a cached wallpaper, e.g.
/// "2026-10-03_OHR.GrizzlySwim_EN-US5133524829.jpg". Anything outside a
/// conservative character set is replaced so names are always safe.
pub fn filename_for(date: &str, id: &str, format: ImageFormat) -> String {
    let stem: String = format!("{date}_{id}")
        .chars()
        .map(|c| match c {
            'a'..='z' | 'A'..='Z' | '0'..='9' | '-' | '_' | '.' => c,
            _ => '_',
        })
        .take(150)
        .collect();
    format!("{}.{}", stem.trim_start_matches('.'), format.extension())
}

/// An in-progress download, deleted on drop unless persisted.
pub struct Download {
    path: PathBuf,
    file: File,
    persisted: bool,
}

impl Download {
    pub fn file(&mut self) -> &mut File {
        &mut self.file
    }

    /// Check that the downloaded data is a complete, supported image.
    pub fn validate(&mut self) -> Result<ImageFormat, Error> {
        detect_format(&mut self.file)
    }

    /// Flush to disk and atomically move into the cache under `filename`.
    pub fn persist(mut self, cache: &Cache, filename: &str) -> Result<PathBuf, Error> {
        let dest = cache.image_path(filename);
        self.file
            .sync_all()
            .and_then(|()| fs::rename(&self.path, &dest))
            .map_err(|e| Error::cache(format!("cannot save {}", dest.display()), e))?;
        self.persisted = true;
        Ok(dest)
    }
}

impl Drop for Download {
    fn drop(&mut self) {
        if !self.persisted {
            let _ = fs::remove_file(&self.path);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageFormat {
    Jpeg,
    Png,
}

impl ImageFormat {
    pub fn extension(self) -> &'static str {
        match self {
            ImageFormat::Jpeg => "jpg",
            ImageFormat::Png => "png",
        }
    }
}

/// No real wallpaper is smaller than this; error pages and placeholders are.
const MIN_IMAGE_BYTES: u64 = 1024;
const PNG_SIGNATURE: [u8; 8] = [0x89, b'P', b'N', b'G', b'\r', b'\n', 0x1A, b'\n'];
/// Final chunk of every PNG: zero length, "IEND", fixed CRC.
const PNG_END: [u8; 12] = [0, 0, 0, 0, b'I', b'E', b'N', b'D', 0xAE, 0x42, 0x60, 0x82];
const JPEG_START: [u8; 3] = [0xFF, 0xD8, 0xFF];
const JPEG_END: [u8; 2] = [0xFF, 0xD9];

/// Identify a JPEG or PNG by its signature and check that it was not
/// truncated by looking for the format's end marker.
pub fn detect_format<R: Read + Seek>(data: &mut R) -> Result<ImageFormat, Error> {
    let io_err = |e| Error::cache("cannot read downloaded image", e);

    let len = data.seek(SeekFrom::End(0)).map_err(io_err)?;
    if len < MIN_IMAGE_BYTES {
        return Err(Error::InvalidImage(format!("only {len} bytes long")));
    }
    let mut head = [0; 8];
    data.seek(SeekFrom::Start(0)).map_err(io_err)?;
    data.read_exact(&mut head).map_err(io_err)?;
    let mut tail = [0; 32];
    data.seek(SeekFrom::End(-(tail.len() as i64)))
        .map_err(io_err)?;
    data.read_exact(&mut tail).map_err(io_err)?;

    if head.starts_with(&JPEG_START) {
        // Some encoders pad after the end-of-image marker, so search the tail.
        if tail.windows(2).any(|w| w == JPEG_END) {
            return Ok(ImageFormat::Jpeg);
        }
        return Err(Error::InvalidImage("JPEG data is truncated".into()));
    }
    if head == PNG_SIGNATURE {
        if tail.ends_with(&PNG_END) {
            return Ok(ImageFormat::Png);
        }
        return Err(Error::InvalidImage("PNG data is truncated".into()));
    }
    Err(Error::InvalidImage("not a JPEG or PNG file".into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn entry(date: &str, name: &str) -> Entry {
        Entry {
            date: date.into(),
            filename: format!("{date}_{name}.jpg"),
            title: Some(name.into()),
            copyright: None,
            source: "bing".into(),
            id: name.into(),
            url: format!("https://example.com/{name}.jpg"),
        }
    }

    fn sample_state(n: usize) -> State {
        let mut state = State::default();
        for day in 1..=n {
            state.insert(entry(&format!("2026-10-{day:02}"), &format!("img{day}")));
        }
        state
    }

    fn jpeg(len: usize) -> Vec<u8> {
        let mut data = vec![0x11; len];
        data[..4].copy_from_slice(&[0xFF, 0xD8, 0xFF, 0xE0]);
        data[len - 2..].copy_from_slice(&JPEG_END);
        data
    }

    /// A unique scratch directory, removed when dropped.
    struct TempDir(PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "daily-wallpaper-test-{name} with spaces & 'quotes'-{}",
                std::process::id()
            ));
            let _ = fs::remove_dir_all(&dir);
            TempDir(dir)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn insert_keeps_order_and_replaces_duplicates() {
        let mut state = State::default();
        state.insert(entry("2026-10-03", "c"));
        state.insert(entry("2026-10-01", "a"));
        state.insert(entry("2026-10-02", "y"));
        state.insert(entry("2026-10-02", "x"));
        state.insert(entry("2026-10-02", "y"));
        let ids: Vec<_> = state.wallpapers.iter().map(|e| e.id.as_str()).collect();
        assert_eq!(ids, ["a", "x", "y", "c"]);
        assert!(state.find("bing", "x").is_some());
        assert!(state.find("other", "x").is_none());
    }

    #[test]
    fn prune_drops_oldest_but_never_current() {
        let mut state = sample_state(10);
        state.current = Some(state.wallpapers[1].filename.clone());
        state.prune(3);
        let kept: Vec<_> = state.wallpapers.iter().map(|e| e.id.as_str()).collect();
        assert_eq!(kept, ["img2", "img9", "img10"]);

        let mut state = sample_state(5);
        state.prune(7);
        assert_eq!(state.wallpapers.len(), 5);
    }

    #[test]
    fn neighbour_wraps_around() {
        let mut state = sample_state(3);
        assert_eq!(state.neighbour(Direction::Next), Some(0));
        assert_eq!(state.neighbour(Direction::Previous), Some(1));
        state.current = Some(state.wallpapers[0].filename.clone());
        assert_eq!(state.neighbour(Direction::Previous), Some(2));
        assert_eq!(state.neighbour(Direction::Next), Some(1));
        assert_eq!(State::default().neighbour(Direction::Next), None);
    }

    #[test]
    fn filenames_are_sanitised() {
        assert_eq!(
            filename_for(
                "2026-10-03",
                "OHR.GrizzlySwim_EN-US5133524829",
                ImageFormat::Jpeg
            ),
            "2026-10-03_OHR.GrizzlySwim_EN-US5133524829.jpg"
        );
        assert_eq!(
            filename_for("2026-10-03", "../../etc/passwd x", ImageFormat::Png),
            "2026-10-03_.._.._etc_passwd_x.png"
        );
        assert!(is_plain_filename("2026-10-03_a.jpg"));
        for bad in ["", ".", "..", "../x.jpg", "a/b.jpg", "/etc/passwd"] {
            assert!(!is_plain_filename(bad), "accepted: {bad}");
        }
    }

    #[test]
    fn detects_valid_images() {
        assert_eq!(
            detect_format(&mut Cursor::new(jpeg(4096))).unwrap(),
            ImageFormat::Jpeg
        );
        // Trailing padding after the end-of-image marker is fine.
        let mut padded = jpeg(4096);
        padded.extend_from_slice(&[0; 10]);
        assert_eq!(
            detect_format(&mut Cursor::new(padded)).unwrap(),
            ImageFormat::Jpeg
        );

        let mut png = PNG_SIGNATURE.to_vec();
        png.resize(4096 - PNG_END.len(), 0x22);
        png.extend_from_slice(&PNG_END);
        assert_eq!(
            detect_format(&mut Cursor::new(png)).unwrap(),
            ImageFormat::Png
        );
    }

    #[test]
    fn rejects_invalid_images() {
        let mut truncated = jpeg(4096);
        truncated.truncate(3000);
        let mut html = b"<!DOCTYPE html><html>".to_vec();
        html.resize(4096, b' ');
        let mut png = PNG_SIGNATURE.to_vec();
        png.resize(4096, 0x22);

        for data in [jpeg(512), truncated, html, png, Vec::new()] {
            assert!(matches!(
                detect_format(&mut Cursor::new(data)),
                Err(Error::InvalidImage(_))
            ));
        }
    }

    #[test]
    fn save_load_round_trip_skips_missing_files() {
        let tmp = TempDir::new("roundtrip");
        let cache = Cache::new(tmp.0.clone());
        assert_eq!(cache.load().unwrap(), State::default());

        let mut state = sample_state(3);
        state.current = Some(state.wallpapers[2].filename.clone());
        let mut traversal = entry("2026-10-04", "evil");
        traversal.filename = "../metadata.json".into();
        state.wallpapers.push(traversal);
        fs::create_dir_all(cache.images_dir()).unwrap();
        for e in &state.wallpapers[1..3] {
            fs::write(cache.image_path(&e.filename), jpeg(2048)).unwrap();
        }
        let _lock = cache.lock().unwrap();
        cache.save(&state).unwrap();

        let loaded = cache.load().unwrap();
        let ids: Vec<_> = loaded.wallpapers.iter().map(|e| e.id.as_str()).collect();
        assert_eq!(ids, ["img2", "img3"]);
        assert_eq!(loaded.current, state.current);
        assert_eq!(loaded.current_index(), Some(1));
    }

    #[test]
    fn corrupt_metadata_is_an_error() {
        let tmp = TempDir::new("corrupt");
        fs::create_dir_all(&tmp.0).unwrap();
        fs::write(tmp.0.join(METADATA_FILE), "{ not json").unwrap();
        assert!(matches!(
            Cache::new(tmp.0.clone()).load(),
            Err(Error::Cache(_))
        ));
    }

    #[test]
    fn download_is_removed_unless_persisted() {
        let tmp = TempDir::new("download");
        let cache = Cache::new(tmp.0.clone());

        let mut download = cache.start_download().unwrap();
        download.file().write_all(&jpeg(2048)).unwrap();
        let temp_path = download.path.clone();
        assert_eq!(download.validate().unwrap(), ImageFormat::Jpeg);
        drop(download);
        assert!(!temp_path.exists());

        let mut download = cache.start_download().unwrap();
        download.file().write_all(&jpeg(2048)).unwrap();
        let dest = download.persist(&cache, "a b's.jpg").unwrap();
        assert_eq!(fs::read(&dest).unwrap(), jpeg(2048));
        assert!(!temp_path.exists());
    }

    #[test]
    fn sweep_removes_unlisted_files_and_stale_downloads() {
        let tmp = TempDir::new("sweep");
        let cache = Cache::new(tmp.0.clone());
        let mut state = sample_state(2);
        state.prune(1);
        fs::create_dir_all(cache.images_dir()).unwrap();
        let listed = cache.image_path(&state.wallpapers[0].filename);
        let pruned = cache.image_path("2026-10-01_img1.jpg");
        let fresh_download = cache.image_path(".download-1.tmp");
        let stale_download = cache.image_path(".download-2.tmp");
        for path in [&listed, &pruned, &fresh_download, &stale_download] {
            fs::write(path, b"x").unwrap();
        }
        let two_hours_ago = std::time::SystemTime::now() - Duration::from_secs(2 * 60 * 60);
        File::options()
            .write(true)
            .open(&stale_download)
            .unwrap()
            .set_modified(two_hours_ago)
            .unwrap();

        cache.sweep(&state);
        assert!(listed.exists());
        assert!(fresh_download.exists());
        assert!(!pruned.exists());
        assert!(!stale_download.exists());
    }

    #[test]
    fn lock_is_exclusive() {
        let tmp = TempDir::new("lock");
        let cache = Cache::new(tmp.0.clone());
        let held = cache.lock().unwrap();
        let file = File::open(tmp.0.join(LOCK_FILE)).unwrap();
        assert!(matches!(file.try_lock(), Err(TryLockError::WouldBlock)));
        drop(held);
        assert!(file.try_lock().is_ok());
    }
}
