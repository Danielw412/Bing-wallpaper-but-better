//! Applies wallpapers to GNOME through `gsettings`.

use std::path::Path;
use std::process::Command;

use reqwest::Url;

use crate::config::Mode;
use crate::error::Error;

const SCHEMA: &str = "org.gnome.desktop.background";

/// Set `image` as both the light- and dark-style GNOME wallpaper.
pub fn set(image: &Path, mode: Mode) -> Result<(), Error> {
    let uri = file_uri(image)?;
    gsettings_set("picture-options", mode.as_str())?;
    gsettings_set("picture-uri", &uri)?;
    gsettings_set("picture-uri-dark", &uri)?;
    Ok(())
}

/// Percent-encoded `file://` URI, so spaces and other special characters in
/// the path survive.
fn file_uri(path: &Path) -> Result<String, Error> {
    Url::from_file_path(path)
        .map(String::from)
        .map_err(|()| Error::Wallpaper(format!("{} is not an absolute path", path.display())))
}

fn gsettings_set(key: &str, value: &str) -> Result<(), Error> {
    // Arguments go straight to the process (no shell); the value is quoted as
    // a GVariant string so gsettings never reinterprets it.
    let output = Command::new("gsettings")
        .args(["set", SCHEMA, key, &gvariant_string(value)])
        .output()
        .map_err(|e| Error::Wallpaper(format!("cannot run gsettings: {e}")))?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        return Err(Error::Wallpaper(format!(
            "gsettings set {SCHEMA} {key} failed ({}): {}",
            output.status,
            stderr.trim()
        )));
    }
    Ok(())
}

/// Quote `s` as a GVariant text-format string literal.
fn gvariant_string(s: &str) -> String {
    let mut quoted = String::with_capacity(s.len() + 2);
    quoted.push('\'');
    for c in s.chars() {
        if c == '\\' || c == '\'' {
            quoted.push('\\');
        }
        quoted.push(c);
    }
    quoted.push('\'');
    quoted
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uri_encodes_special_characters() {
        let uri = file_uri(Path::new("/home/a b/Pics #1/it's 100%.jpg")).unwrap();
        assert_eq!(uri, "file:///home/a%20b/Pics%20%231/it's%20100%25.jpg");
        assert!(file_uri(Path::new("relative.jpg")).is_err());
    }

    #[test]
    fn gvariant_strings_are_escaped() {
        assert_eq!(gvariant_string("zoom"), "'zoom'");
        assert_eq!(
            gvariant_string(r"file:///it's\here"),
            r"'file:///it\'s\\here'"
        );
    }
}
