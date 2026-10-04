use std::io::{Read, Write};
use std::thread;
use std::time::Duration;

use reqwest::blocking::{Client, Response};
use reqwest::header::CONTENT_TYPE;
use reqwest::{StatusCode, Url};

use crate::error::Error;

const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
/// Whole-request timeout for small metadata documents.
pub const METADATA_TIMEOUT: Duration = Duration::from_secs(30);
/// Whole-request timeout for image downloads (a UHD JPEG is a few MiB).
pub const IMAGE_TIMEOUT: Duration = Duration::from_secs(120);
/// Refuse to store anything larger than this.
const MAX_IMAGE_BYTES: u64 = 50 * 1024 * 1024;
/// Pauses between attempts after a transient failure. Covers the window after
/// boot or resume where the timer fires before Wi-Fi has reconnected, while
/// still giving up after about two minutes.
const RETRY_DELAYS: [Duration; 4] = [
    Duration::from_secs(5),
    Duration::from_secs(15),
    Duration::from_secs(30),
    Duration::from_secs(60),
];

pub fn client() -> Result<Client, Error> {
    Client::builder()
        .user_agent(concat!("daily-wallpaper/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(CONNECT_TIMEOUT)
        .timeout(METADATA_TIMEOUT)
        .https_only(true)
        .build()
        .map_err(|e| Error::Network(format!("cannot initialise HTTP client: {}", describe(&e))))
}

/// GET `url` and return the response if it has a 2xx status. Connection
/// failures, timeouts, 5xx and 429 responses are retried a bounded number of
/// times; anything else fails immediately.
pub fn get(client: &Client, url: &Url, timeout: Duration) -> Result<Response, Error> {
    let mut delays = RETRY_DELAYS.iter();
    loop {
        let failure = match client.get(url.clone()).timeout(timeout).send() {
            Ok(response) if response.status().is_success() => return Ok(response),
            Ok(response) => {
                let status = response.status();
                let msg = format!("{url} returned HTTP {status}");
                if !(status.is_server_error() || status == StatusCode::TOO_MANY_REQUESTS) {
                    return Err(Error::Network(msg));
                }
                msg
            }
            Err(e) => {
                let msg = describe(&e);
                if !(e.is_connect() || e.is_timeout()) {
                    return Err(Error::Network(msg));
                }
                msg
            }
        };
        let Some(delay) = delays.next() else {
            return Err(Error::Network(failure));
        };
        eprintln!(
            "daily-wallpaper: {failure}; retrying in {}s",
            delay.as_secs()
        );
        thread::sleep(*delay);
    }
}

/// Download an image from `url` into `out`, returning the number of bytes.
pub fn download(client: &Client, url: &Url, out: &mut impl Write) -> Result<u64, Error> {
    let mut response = get(client, url, IMAGE_TIMEOUT)?;

    if let Some(content_type) = response.headers().get(CONTENT_TYPE)
        && !content_type.as_bytes().starts_with(b"image/")
    {
        return Err(Error::InvalidImage(format!(
            "{url} served {:?} instead of an image",
            String::from_utf8_lossy(content_type.as_bytes())
        )));
    }
    let expected = response.content_length();

    let mut buf = vec![0; 64 * 1024];
    let mut total: u64 = 0;
    loop {
        let n = response
            .read(&mut buf)
            .map_err(|e| Error::Network(format!("download of {url} interrupted: {e}")))?;
        if n == 0 {
            break;
        }
        total += n as u64;
        if total > MAX_IMAGE_BYTES {
            return Err(Error::InvalidImage(format!(
                "{url} is larger than {} MiB",
                MAX_IMAGE_BYTES / 1024 / 1024
            )));
        }
        out.write_all(&buf[..n])
            .map_err(|e| Error::cache("cannot write downloaded image", e))?;
    }
    if let Some(expected) = expected
        && expected != total
    {
        return Err(Error::Network(format!(
            "download of {url} incomplete: got {total} of {expected} bytes"
        )));
    }
    Ok(total)
}

/// Render an error with its root cause, e.g. "error sending request for url
/// (...): Connection refused (os error 111)". The layers in between add noise.
fn describe(err: &reqwest::Error) -> String {
    let mut root = std::error::Error::source(err);
    while let Some(next) = root.and_then(|cause| cause.source()) {
        root = Some(next);
    }
    match root {
        Some(cause) => format!("{err}: {cause}"),
        None => err.to_string(),
    }
}
