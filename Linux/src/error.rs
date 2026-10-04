use std::fmt;

/// Every failure the program can report. Each kind maps to its own exit code
/// so scripts and `systemctl --user status` can tell them apart.
#[derive(Debug)]
pub enum Error {
    /// Bad command-line usage.
    Usage(String),
    /// The configuration file could not be read or is invalid.
    Config(String),
    /// Fetching metadata or the image failed, or the provider sent nonsense.
    Network(String),
    /// The downloaded file is not a supported, complete image.
    InvalidImage(String),
    /// `gsettings` could not apply the wallpaper.
    Wallpaper(String),
    /// Reading or writing the local cache or its metadata failed.
    Cache(String),
    /// Another instance kept the lock for too long.
    Locked,
}

impl Error {
    pub fn exit_code(&self) -> u8 {
        match self {
            Error::Usage(_) => 2,
            Error::Config(_) => 3,
            Error::Network(_) => 4,
            Error::InvalidImage(_) => 5,
            Error::Wallpaper(_) => 6,
            Error::Cache(_) => 7,
            Error::Locked => 8,
        }
    }

    /// Shorthand for wrapping an I/O error with what we were doing.
    pub fn cache(context: impl fmt::Display, err: std::io::Error) -> Self {
        Error::Cache(format!("{context}: {err}"))
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Usage(msg) => write!(f, "{msg}"),
            Error::Config(msg) => write!(f, "config error: {msg}"),
            Error::Network(msg) => write!(f, "network error: {msg}"),
            Error::InvalidImage(msg) => write!(f, "invalid image: {msg}"),
            Error::Wallpaper(msg) => write!(f, "cannot set wallpaper: {msg}"),
            Error::Cache(msg) => write!(f, "cache error: {msg}"),
            Error::Locked => write!(f, "another daily-wallpaper process is still running"),
        }
    }
}

impl std::error::Error for Error {}
