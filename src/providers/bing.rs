use reqwest::Url;
use reqwest::blocking::Client;
use serde::Deserialize;

use super::{RemoteWallpaper, WallpaperProvider};
use crate::error::Error;
use crate::http;

const ORIGIN: &str = "https://www.bing.com";
const ARCHIVE_PATH: &str = "/HPImageArchive.aspx";

/// Bing's "image of the day".
pub struct Bing {
    market: String,
    resolution: String,
}

impl Bing {
    pub fn new(market: &str, resolution: &str) -> Self {
        Bing {
            market: market.to_owned(),
            resolution: resolution.to_owned(),
        }
    }

    fn archive_url(&self) -> Url {
        let params = [
            ("format", "js"),
            ("idx", "0"),
            ("n", "1"),
            ("mkt", self.market.as_str()),
        ];
        Url::parse_with_params(&format!("{ORIGIN}{ARCHIVE_PATH}"), params)
            .expect("Bing archive URL is valid")
    }
}

impl WallpaperProvider for Bing {
    fn name(&self) -> &'static str {
        "bing"
    }

    fn current(&self, client: &Client) -> Result<RemoteWallpaper, Error> {
        let body = http::get(client, &self.archive_url(), http::METADATA_TIMEOUT)?
            .text()
            .map_err(|e| Error::Network(format!("cannot read Bing response: {e}")))?;
        parse(&body, &self.resolution)
    }
}

#[derive(Deserialize)]
struct Archive {
    #[serde(default)]
    images: Vec<Image>,
}

#[derive(Deserialize)]
struct Image {
    startdate: String,
    /// e.g. "/th?id=OHR.GrizzlySwim_EN-US5133524829_1920x1080.jpg&rf=...&pid=hp"
    url: Option<String>,
    /// e.g. "/th?id=OHR.GrizzlySwim_EN-US5133524829"; append "_UHD.jpg" etc.
    urlbase: Option<String>,
    title: Option<String>,
    copyright: Option<String>,
}

fn parse(body: &str, resolution: &str) -> Result<RemoteWallpaper, Error> {
    let bad = |msg: String| Error::Network(format!("unexpected response from Bing: {msg}"));

    let archive: Archive = serde_json::from_str(body).map_err(|e| bad(e.to_string()))?;
    let image = archive
        .images
        .into_iter()
        .next()
        .ok_or_else(|| bad("no images listed".into()))?;
    let date = format_date(&image.startdate)
        .ok_or_else(|| bad(format!("invalid startdate {:?}", image.startdate)))?;

    // Prefer `urlbase`, which lets us pick the size; fall back to the
    // ready-made (1920x1080) `url`.
    let non_empty = |s: Option<String>| s.filter(|s| !s.trim().is_empty());
    let (id_path, image_path) = match (non_empty(image.urlbase), non_empty(image.url)) {
        (Some(base), _) => (base.clone(), format!("{base}_{resolution}.jpg")),
        (None, Some(url)) => (url.clone(), url),
        (None, None) => return Err(bad("image has no URL".into())),
    };

    let origin = Url::parse(ORIGIN).expect("Bing origin is valid");
    let url = origin
        .join(&image_path)
        .map_err(|e| bad(format!("invalid image URL {image_path:?}: {e}")))?;
    // The `id` query parameter (e.g. "OHR.GrizzlySwim_EN-US5133524829")
    // names the image; fall back to the whole path if it is missing.
    let id = origin
        .join(&id_path)
        .ok()
        .and_then(|u| {
            u.query_pairs()
                .find(|(key, _)| key == "id")
                .map(|(_, value)| value.into_owned())
        })
        .unwrap_or(id_path);

    Ok(RemoteWallpaper {
        id,
        date,
        title: non_empty(image.title),
        copyright: non_empty(image.copyright),
        url,
    })
}

/// "20261003" -> "2026-10-03"
fn format_date(raw: &str) -> Option<String> {
    if raw.len() != 8 || !raw.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    Some(format!("{}-{}-{}", &raw[0..4], &raw[4..6], &raw[6..8]))
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{"images":[{"startdate":"20261003","fullstartdate":"202610030700","enddate":"20261004","url":"/th?id=OHR.GrizzlySwim_EN-US5133524829_1920x1080.jpg&rf=LaDigue_1920x1080.jpg&pid=hp","urlbase":"/th?id=OHR.GrizzlySwim_EN-US5133524829","copyright":"Brown bear in Silver Salmon Creek, Lake Clark National Park and Preserve, Alaska (© Danny Green/Nature Picture Library)","copyrightlink":"https://www.bing.com/search?q=brown+bear","title":"Catch, eat, repeat","quiz":"/search?q=Bing+homepage+quiz","wp":true,"hsh":"d35d1a653af842c69073af14002779c3","drk":1,"top":1,"bot":1,"hs":[]}],"tooltips":{"loading":"Loading..."}}"#;

    #[test]
    fn parses_current_image() {
        let wallpaper = parse(SAMPLE, "UHD").unwrap();
        assert_eq!(wallpaper.id, "OHR.GrizzlySwim_EN-US5133524829");
        assert_eq!(wallpaper.date, "2026-10-03");
        assert_eq!(wallpaper.title.as_deref(), Some("Catch, eat, repeat"));
        assert!(wallpaper.copyright.unwrap().starts_with("Brown bear"));
        assert_eq!(
            wallpaper.url.as_str(),
            "https://www.bing.com/th?id=OHR.GrizzlySwim_EN-US5133524829_UHD.jpg"
        );
    }

    #[test]
    fn honours_resolution() {
        let wallpaper = parse(SAMPLE, "1920x1080").unwrap();
        assert!(wallpaper.url.as_str().ends_with("_1920x1080.jpg"));
    }

    #[test]
    fn falls_back_to_url_without_urlbase() {
        let body = r#"{"images":[{"startdate":"20261003","url":"/th?id=OHR.Foo_EN-US1_1920x1080.jpg&pid=hp","title":""}]}"#;
        let wallpaper = parse(body, "UHD").unwrap();
        assert_eq!(wallpaper.id, "OHR.Foo_EN-US1_1920x1080.jpg");
        assert_eq!(
            wallpaper.url.as_str(),
            "https://www.bing.com/th?id=OHR.Foo_EN-US1_1920x1080.jpg&pid=hp"
        );
        assert_eq!(wallpaper.title, None);
        assert_eq!(wallpaper.copyright, None);
    }

    #[test]
    fn rejects_malformed_responses() {
        for body in [
            "",
            "<html>captive portal</html>",
            r#"{"images":[]}"#,
            r#"{"images":[{"startdate":"2026-10-03","urlbase":"/th?id=X"}]}"#,
            r#"{"images":[{"startdate":"20261003"}]}"#,
        ] {
            assert!(parse(body, "UHD").is_err(), "accepted: {body}");
        }
    }

    #[test]
    fn archive_url_encodes_market() {
        let url = Bing::new("en-US", "UHD").archive_url();
        assert_eq!(
            url.as_str(),
            "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US"
        );
    }
}
