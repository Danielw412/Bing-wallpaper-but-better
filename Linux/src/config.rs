use std::env;
use std::fs;
use std::io::ErrorKind;
use std::path::{Path, PathBuf};

use serde::Deserialize;

use crate::error::Error;

const APP_DIR: &str = "daily-wallpaper";

/// Where the program reads its configuration and keeps its data, following
/// the XDG base directory spec (`~/.config`, `~/.local/share` by default).
pub struct Paths {
    pub config_file: PathBuf,
    pub data_dir: PathBuf,
}

impl Paths {
    pub fn from_env() -> Result<Self, Error> {
        let home = env::var_os("HOME")
            .filter(|h| !h.is_empty())
            .map(PathBuf::from);
        let config_home = xdg_dir("XDG_CONFIG_HOME", home.as_deref(), ".config")?;
        let data_home = xdg_dir("XDG_DATA_HOME", home.as_deref(), ".local/share")?;
        Ok(Paths {
            config_file: config_home.join(APP_DIR).join("config.toml"),
            data_dir: data_home.join(APP_DIR),
        })
    }
}

/// Resolve an XDG base directory. Relative values are invalid per the spec
/// and are ignored in favour of the `$HOME` fallback.
fn xdg_dir(var: &str, home: Option<&Path>, fallback: &str) -> Result<PathBuf, Error> {
    match env::var_os(var).map(PathBuf::from) {
        Some(dir) if dir.is_absolute() => Ok(dir),
        _ => home
            .map(|h| h.join(fallback))
            .ok_or_else(|| Error::Config(format!("neither {var} nor HOME is set"))),
    }
}

#[derive(Debug, PartialEq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Config {
    pub provider: ProviderKind,
    /// Bing market, e.g. "en-US".
    pub market: String,
    /// Bing image size suffix: "UHD" or e.g. "1920x1080".
    pub resolution: String,
    pub cache: CacheConfig,
    pub wallpaper: WallpaperConfig,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProviderKind {
    Bing,
}

#[derive(Debug, PartialEq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct CacheConfig {
    /// How many recent wallpapers to keep on disk.
    pub keep: usize,
}

#[derive(Debug, PartialEq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct WallpaperConfig {
    pub mode: Mode,
}

/// Values accepted by `org.gnome.desktop.background picture-options`
/// (excluding "none", which hides the picture entirely).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    Wallpaper,
    Centered,
    Scaled,
    Stretched,
    Zoom,
    Spanned,
}

impl Mode {
    pub fn as_str(self) -> &'static str {
        match self {
            Mode::Wallpaper => "wallpaper",
            Mode::Centered => "centered",
            Mode::Scaled => "scaled",
            Mode::Stretched => "stretched",
            Mode::Zoom => "zoom",
            Mode::Spanned => "spanned",
        }
    }
}

impl Default for Config {
    fn default() -> Self {
        Config {
            provider: ProviderKind::Bing,
            market: "en-US".into(),
            resolution: "UHD".into(),
            cache: CacheConfig::default(),
            wallpaper: WallpaperConfig::default(),
        }
    }
}

impl Default for CacheConfig {
    fn default() -> Self {
        CacheConfig { keep: 7 }
    }
}

impl Default for WallpaperConfig {
    fn default() -> Self {
        WallpaperConfig { mode: Mode::Zoom }
    }
}

impl Config {
    /// Load the config file, falling back to defaults if it does not exist.
    pub fn load(path: &Path) -> Result<Self, Error> {
        match fs::read_to_string(path) {
            Ok(text) => Self::parse(&text)
                .map_err(|msg| Error::Config(format!("{}: {msg}", path.display()))),
            Err(e) if e.kind() == ErrorKind::NotFound => Ok(Config::default()),
            Err(e) => Err(Error::Config(format!(
                "cannot read {}: {e}",
                path.display()
            ))),
        }
    }

    fn parse(text: &str) -> Result<Self, String> {
        let config: Config =
            toml::from_str(text).map_err(|e| e.to_string().trim_end().to_owned())?;
        config.validate()?;
        Ok(config)
    }

    fn validate(&self) -> Result<(), String> {
        if self.cache.keep == 0 {
            return Err("cache.keep must be at least 1".into());
        }
        let market_ok = !self.market.is_empty()
            && self
                .market
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '-');
        if !market_ok {
            return Err(format!(
                "invalid market {:?}; expected something like \"en-US\"",
                self.market
            ));
        }
        let resolution_ok = !self.resolution.is_empty()
            && self.resolution.chars().all(|c| c.is_ascii_alphanumeric());
        if !resolution_ok {
            return Err(format!(
                "invalid resolution {:?}; expected \"UHD\" or e.g. \"1920x1080\"",
                self.resolution
            ));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_documented_example() {
        let config = Config::parse(
            r#"
            provider = "bing"
            market = "en-GB"

            [cache]
            keep = 3

            [wallpaper]
            mode = "scaled"
            "#,
        )
        .unwrap();
        assert_eq!(config.provider, ProviderKind::Bing);
        assert_eq!(config.market, "en-GB");
        assert_eq!(config.resolution, "UHD");
        assert_eq!(config.cache.keep, 3);
        assert_eq!(config.wallpaper.mode, Mode::Scaled);
    }

    #[test]
    fn empty_file_uses_defaults() {
        assert_eq!(Config::parse("").unwrap(), Config::default());
        let config = Config::default();
        assert_eq!(config.cache.keep, 7);
        assert_eq!(config.wallpaper.mode, Mode::Zoom);
    }

    #[test]
    fn shipped_default_config_matches_defaults() {
        let shipped = include_str!("../dist/config.toml");
        assert_eq!(Config::parse(shipped).unwrap(), Config::default());
    }

    #[test]
    fn rejects_invalid_values() {
        for bad in [
            "provider = \"unsplash\"",
            "market = \"\"",
            "market = \"en US\"",
            "resolution = \"../x\"",
            "[cache]\nkeep = 0",
            "[wallpaper]\nmode = \"none\"",
            "[wallpaper]\nmod = \"zoom\"",
            "typo = 1",
        ] {
            assert!(Config::parse(bad).is_err(), "accepted: {bad}");
        }
    }

    #[test]
    fn missing_file_uses_defaults() {
        let path = Path::new("/nonexistent/daily-wallpaper/config.toml");
        assert_eq!(Config::load(path).unwrap(), Config::default());
    }
}
