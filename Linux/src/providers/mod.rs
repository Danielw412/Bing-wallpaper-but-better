mod bing;

use reqwest::Url;
use reqwest::blocking::Client;

use crate::config::{Config, ProviderKind};
use crate::error::Error;

/// Description of the wallpaper a provider is currently offering.
#[derive(Debug, Clone, PartialEq)]
pub struct RemoteWallpaper {
    /// Identifier that is unique and stable for this image within the
    /// provider; used to tell whether it is already cached.
    pub id: String,
    /// Publication date as YYYY-MM-DD.
    pub date: String,
    pub title: Option<String>,
    pub copyright: Option<String>,
    /// Where to download the image from.
    pub url: Url,
}

/// A source of daily wallpapers.
pub trait WallpaperProvider {
    /// Short name recorded as the `source` of cached wallpapers.
    fn name(&self) -> &'static str;

    /// Fetch metadata for the provider's current wallpaper. This must not
    /// download the image itself.
    fn current(&self, client: &Client) -> Result<RemoteWallpaper, Error>;
}

pub fn from_config(config: &Config) -> Box<dyn WallpaperProvider> {
    match config.provider {
        ProviderKind::Bing => Box::new(bing::Bing::new(&config.market, &config.resolution)),
    }
}
