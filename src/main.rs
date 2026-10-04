//! daily-wallpaper: set the Bing image of the day as the GNOME wallpaper.
//!
//! Runs once and exits; a systemd user timer invokes `update` daily.

mod cache;
mod config;
mod error;
mod http;
mod providers;
mod wallpaper;

use std::process::ExitCode;

use cache::{Cache, Direction, Entry};
use config::{Config, Paths};
use error::Error;

const USAGE: &str = "\
Usage: daily-wallpaper <command>

Commands:
  update     Download the provider's current wallpaper if it is new, and apply it
  info       Show the applied wallpaper and cache status
  previous   Apply the previous (older) cached wallpaper
  next       Apply the next (newer) cached wallpaper

Options:
  -h, --help     Show this help
  -V, --version  Show the version
";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args_os()
        .skip(1)
        .map(|a| a.to_string_lossy().into_owned())
        .collect();
    match run(&args) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("daily-wallpaper: {e}");
            if matches!(e, Error::Usage(_)) {
                eprint!("\n{USAGE}");
            }
            ExitCode::from(e.exit_code())
        }
    }
}

fn run(args: &[String]) -> Result<(), Error> {
    let command = match args {
        [command] => command.as_str(),
        [] => return Err(Error::Usage("missing command".into())),
        [_, extra, ..] => return Err(Error::Usage(format!("unexpected argument '{extra}'"))),
    };
    match command {
        "-h" | "--help" | "help" => {
            print!("{USAGE}");
            return Ok(());
        }
        "-V" | "--version" => {
            println!("daily-wallpaper {}", env!("CARGO_PKG_VERSION"));
            return Ok(());
        }
        "update" | "info" | "previous" | "next" => {}
        other => return Err(Error::Usage(format!("unknown command '{other}'"))),
    }

    let paths = Paths::from_env()?;
    let config = Config::load(&paths.config_file)?;
    let cache = Cache::new(paths.data_dir);
    match command {
        "update" => update(&config, &cache),
        "info" => info(&config, &cache),
        "previous" => step(&config, &cache, Direction::Previous),
        _ => step(&config, &cache, Direction::Next),
    }
}

fn update(config: &Config, cache: &Cache) -> Result<(), Error> {
    let provider = providers::from_config(config);
    let client = http::client()?;
    let remote = provider.current(&client)?;

    // Network work happens without the lock so `previous`/`next` stay
    // responsive; the state is re-checked once the lock is held.
    if let Some(entry) = cache.load()?.find(provider.name(), &remote.id) {
        println!("Already up to date: {}", entry.label());
        return Ok(());
    }

    let mut download = cache.start_download()?;
    http::download(&client, &remote.url, download.file())?;
    let format = download.validate()?;

    let _lock = cache.lock()?;
    let mut state = cache.load()?;
    if let Some(entry) = state.find(provider.name(), &remote.id) {
        println!("Already up to date: {}", entry.label());
        return Ok(());
    }

    let filename = cache::filename_for(&remote.date, &remote.id, format);
    let path = download.persist(cache, &filename)?;
    // On failure the new file stays untracked (GNOME may already point at it
    // if only some keys were set); the next successful run sweeps it away.
    wallpaper::set(&path, config.wallpaper.mode)?;

    let entry = Entry {
        date: remote.date,
        filename: filename.clone(),
        title: remote.title,
        copyright: remote.copyright,
        source: provider.name().to_owned(),
        id: remote.id,
        url: remote.url.into(),
    };
    let label = entry.label();
    state.insert(entry);
    state.current = Some(filename);
    state.prune(config.cache.keep);
    // Save before deleting so the metadata never lists missing files.
    cache.save(&state)?;
    cache.sweep(&state);

    println!("Wallpaper set: {label}");
    Ok(())
}

fn step(config: &Config, cache: &Cache, direction: Direction) -> Result<(), Error> {
    let _lock = cache.lock()?;
    let mut state = cache.load()?;
    let index = state.neighbour(direction).ok_or_else(|| {
        Error::Cache("no cached wallpapers yet; run 'daily-wallpaper update' first".into())
    })?;
    let entry = &state.wallpapers[index];
    wallpaper::set(&cache.image_path(&entry.filename), config.wallpaper.mode)?;

    println!(
        "Wallpaper set: {} ({} of {})",
        entry.label(),
        index + 1,
        state.wallpapers.len()
    );
    state.current = Some(entry.filename.clone());
    cache.save(&state)
}

fn info(config: &Config, cache: &Cache) -> Result<(), Error> {
    let state = cache.load()?;
    let count = state.wallpapers.len();
    match state.current_index() {
        Some(index) => {
            let entry = &state.wallpapers[index];
            let newest = if index + 1 == count { " (newest)" } else { "" };
            if let Some(title) = &entry.title {
                println!("Title:      {title}");
            }
            if let Some(copyright) = &entry.copyright {
                println!("Copyright:  {copyright}");
            }
            println!("Date:       {}", entry.date);
            println!("Source:     {}", entry.source);
            println!(
                "File:       {}",
                cache.image_path(&entry.filename).display()
            );
            println!("URL:        {}", entry.url);
            println!("Position:   {} of {count}{newest}", index + 1);
        }
        None if count == 0 => {
            println!("No wallpapers cached yet; run 'daily-wallpaper update'.");
        }
        None => println!("Cached:     {count} wallpaper(s); none applied by daily-wallpaper"),
    }
    println!(
        "Cache:      {} (keeping {})",
        cache.dir().display(),
        config.cache.keep
    );
    Ok(())
}
