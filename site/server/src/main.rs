//! provance-status: serves `/api/status` for the demo site.
//!
//! Polls GitHub Actions and releases, NuGet, Maven Central and PyPI in the background and keeps the last good
//! answer of each source in memory, so a registry that is down never empties the page. Requests from the site
//! are served from memory: visitors never hit the upstream APIs.
//!
//! Configuration (environment variables):
//!   PROVANCE_STATUS_ADDR   listen address, default 127.0.0.1:8787 (put Caddy or nginx in front)
//!   PROVANCE_REPOSITORY    GitHub repository, default h-ibrahim365/ProvanceProtocol
//!   GITHUB_TOKEN           optional; raises the GitHub limit from 60 to 5000 requests per hour.
//!                          A fine-grained token with no permissions is enough for a public repository.

mod model;
mod sources;

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use axum::extract::State;
use axum::http::{HeaderValue, header};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::{Json, Router};
use serde_json::Value;
use tokio::sync::RwLock;
use tracing::{info, warn};

use model::{Implementation, Package, Release, Run, Snapshot, SourceState};

const USER_AGENT: &str = concat!(
    "provance-status/",
    env!("CARGO_PKG_VERSION"),
    " (+https://github.com/h-ibrahim365/ProvanceProtocol)"
);
const REGISTRY_INTERVAL: Duration = Duration::from_secs(120);

#[derive(Default)]
struct Cache {
    nuget: Vec<Package>,
    maven: Vec<Package>,
    pypi: Vec<Package>,
    runs: Vec<Run>,
    releases: Vec<Release>,
    sources: BTreeMap<&'static str, SourceState>,
}

struct App {
    http: reqwest::Client,
    repository: String,
    token: Option<String>,
    cache: RwLock<Cache>,
    snapshot: RwLock<Option<Snapshot>>,
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            std::env::var("RUST_LOG").unwrap_or_else(|_| "provance_status=info".into()),
        )
        .init();
    rustls::crypto::ring::default_provider()
        .install_default()
        .expect("install the TLS provider");

    let addr = std::env::var("PROVANCE_STATUS_ADDR").unwrap_or_else(|_| "127.0.0.1:8787".into());
    let app = Arc::new(App {
        http: reqwest::Client::builder()
            .user_agent(USER_AGENT)
            .timeout(Duration::from_secs(15))
            .build()
            .expect("HTTP client"),
        repository: std::env::var("PROVANCE_REPOSITORY")
            .unwrap_or_else(|_| "h-ibrahim365/ProvanceProtocol".into()),
        token: std::env::var("GITHUB_TOKEN")
            .ok()
            .filter(|t| !t.trim().is_empty()),
        cache: RwLock::default(),
        snapshot: RwLock::default(),
    });
    if app.token.is_none() {
        warn!(
            "GITHUB_TOKEN is not set: GitHub is polled slowly to stay under 60 requests per hour"
        );
    }

    tokio::spawn(poll(app.clone()));

    let router = Router::new()
        .route("/api/status", get(status))
        .route("/api/health", get(|| async { "ok" }))
        .with_state(app);
    let listener = tokio::net::TcpListener::bind(&addr)
        .await
        .expect("bind the listen address");
    info!("listening on {addr}");
    axum::serve(listener, router)
        .with_graceful_shutdown(shutdown())
        .await
        .expect("server");
}

async fn shutdown() {
    let ctrl_c = tokio::signal::ctrl_c();
    let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
        .expect("SIGTERM handler");
    tokio::select! { _ = ctrl_c => {}, _ = term.recv() => {} }
}

async fn status(State(app): State<Arc<App>>) -> Response {
    match app.snapshot.read().await.clone() {
        Some(snapshot) => {
            let mut response = Json(snapshot).into_response();
            response
                .headers_mut()
                .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
            response
        }
        None => (
            axum::http::StatusCode::SERVICE_UNAVAILABLE,
            "status not loaded yet",
        )
            .into_response(),
    }
}

/// GitHub every 30 s (10 s while a run is active); registries every 2 minutes, and right after a run finishes
/// so a new version shows up as soon as the registry has it. Without a token, GitHub is polled every 90 s.
async fn poll(app: Arc<App>) {
    let mut last_registries: Option<Instant> = None;
    let mut was_active = false;
    loop {
        refresh_github(&app).await;
        let active = app.cache.read().await.runs.iter().any(Run::is_active);
        let due = last_registries.is_none_or(|t| t.elapsed() >= REGISTRY_INTERVAL);
        if due || (was_active && !active) {
            refresh_registries(&app).await;
            last_registries = Some(Instant::now());
        }
        was_active = active;
        publish(&app).await;

        let wait = match (app.token.is_some(), active) {
            (true, true) => 10,
            (true, false) => 30,
            (false, _) => 90,
        };
        tokio::time::sleep(Duration::from_secs(wait)).await;
    }
}

async fn refresh_github(app: &App) {
    let base = format!("https://api.github.com/repos/{}", app.repository);
    let result = async {
        let runs = sources::runs(&github(app, &format!("{base}/actions/runs?per_page=40")).await?);
        let releases =
            sources::releases(&github(app, &format!("{base}/releases?per_page=30")).await?);
        Ok::<_, String>((runs, releases))
    }
    .await;
    let mut cache = app.cache.write().await;
    match result {
        Ok((runs, releases)) => {
            cache.runs = runs;
            cache.releases = releases;
            record(&mut cache, "github", None);
        }
        Err(e) => record(&mut cache, "github", Some(e)),
    }
}

async fn github(app: &App, url: &str) -> Result<Value, String> {
    let mut request = app
        .http
        .get(url)
        .header(header::ACCEPT, "application/vnd.github+json")
        .header("X-GitHub-Api-Version", "2022-11-28");
    if let Some(token) = &app.token {
        request = request.bearer_auth(token);
    }
    json(request).await
}

async fn refresh_registries(app: &App) {
    let nuget = async {
        let mut out = Vec::new();
        for id in sources::NUGET_PACKAGES {
            let index = json(app.http.get(sources::nuget_url(id))).await?;
            let package = match sources::nuget_latest(id, &index) {
                Ok(p) => p,
                Err(pages) => {
                    let mut fetched = Vec::new();
                    for page in pages {
                        fetched.push(json(app.http.get(&page)).await?);
                    }
                    sources::nuget_from_pages(id, &fetched)
                }
            };
            out.extend(package);
        }
        Ok::<_, String>(out)
    };
    let maven = async {
        let mut out = Vec::new();
        for artifact in sources::MAVEN_ARTIFACTS {
            let xml = text(app.http.get(sources::maven_url(artifact))).await?;
            out.extend(sources::maven_latest(artifact, &xml));
        }
        Ok::<_, String>(out)
    };
    let pypi = async {
        let body = json(app.http.get(sources::pypi_url(sources::PYPI_PACKAGE))).await?;
        Ok::<_, String>(sources::pypi_latest(&body).into_iter().collect())
    };
    let (nuget, maven, pypi) = tokio::join!(nuget, maven, pypi);

    let mut cache = app.cache.write().await;
    for (name, result) in [("nuget", nuget), ("maven", maven), ("pypi", pypi)] {
        match result {
            Ok(packages) => {
                match name {
                    "nuget" => cache.nuget = packages,
                    "maven" => cache.maven = packages,
                    _ => cache.pypi = packages,
                }
                record(&mut cache, name, None);
            }
            Err(e) => record(&mut cache, name, Some(e)),
        }
    }
}

async fn json(request: reqwest::RequestBuilder) -> Result<Value, String> {
    let body = text(request).await?;
    serde_json::from_str(&body).map_err(|e| format!("invalid JSON: {e}"))
}

async fn text(request: reqwest::RequestBuilder) -> Result<String, String> {
    let response = request.send().await.map_err(describe)?;
    let status = response.status();
    if !status.is_success() {
        return Err(format!("HTTP {status}"));
    }
    response.text().await.map_err(|e| e.to_string())
}

/// The error and its causes on one line, without the URL (it may hold a token in other setups).
fn describe(error: reqwest::Error) -> String {
    let error = error.without_url();
    let mut text = error.to_string();
    let mut source = std::error::Error::source(&error);
    while let Some(cause) = source {
        text.push_str(": ");
        text.push_str(&cause.to_string());
        source = cause.source();
    }
    text
}

/// Keeps the previous data of a source when it fails; only its state changes.
fn record(cache: &mut Cache, source: &'static str, error: Option<String>) {
    if let Some(e) = &error {
        warn!("{source}: {e}");
    }
    let previous = cache.sources.get(source).and_then(|s| s.checked_at.clone());
    cache.sources.insert(
        source,
        SourceState {
            ok: error.is_none(),
            checked_at: if error.is_none() {
                Some(now())
            } else {
                previous
            },
            error,
        },
    );
}

async fn publish(app: &App) {
    let cache = app.cache.read().await;
    let snapshot = build(&app.repository, &cache);
    *app.snapshot.write().await = Some(snapshot);
}

fn build(repository: &str, cache: &Cache) -> Snapshot {
    let pipeline = |language: &str| {
        let path = sources::RELEASE_WORKFLOWS
            .iter()
            .find(|(l, _)| *l == language)
            .map(|(_, p)| *p)?;
        cache.runs.iter().find(|r| r.path == path).cloned()
    };
    let release = |language: &str| {
        cache
            .releases
            .iter()
            .find(|r| r.language == language)
            .cloned()
    };
    let implementation = |id, name, registry, packages: &Vec<Package>| Implementation {
        id,
        name,
        registry,
        packages: packages.clone(),
        release: release(id),
        pipeline: pipeline(id),
    };
    Snapshot {
        generated_at: now(),
        repository: repository.to_owned(),
        active: cache.runs.iter().any(Run::is_active),
        implementations: vec![
            implementation("dotnet", ".NET", "NuGet", &cache.nuget),
            implementation("java", "Java", "Maven Central", &cache.maven),
            implementation("python", "Python", "PyPI", &cache.pypi),
        ],
        runs: cache.runs.iter().take(12).cloned().collect(),
        sources: cache.sources.clone(),
    }
}

fn now() -> String {
    humantime::format_rfc3339_seconds(SystemTime::now()).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_links_pipelines_and_releases_to_their_implementation() {
        let read = |n: &str| -> Value {
            serde_json::from_str(
                &std::fs::read_to_string(format!(
                    "{}/tests/fixtures/{n}",
                    env!("CARGO_MANIFEST_DIR")
                ))
                .unwrap(),
            )
            .unwrap()
        };
        let cache = Cache {
            runs: sources::runs(&read("github-runs.json")),
            releases: sources::releases(&read("github-releases.json")),
            pypi: sources::pypi_latest(&read("pypi.json"))
                .into_iter()
                .collect(),
            ..Cache::default()
        };
        let s = build("h-ibrahim365/ProvanceProtocol", &cache);
        assert!(s.active);
        let python = &s.implementations[2];
        assert_eq!(python.pipeline.as_ref().unwrap().status, "in_progress");
        assert_eq!(python.release.as_ref().unwrap().tag, "python-v0.1.0");
        assert_eq!(python.packages[0].version, "0.1.0");
        // the skipped Java run is ignored: the pipeline shown is the previous real one
        assert_eq!(
            s.implementations[1]
                .pipeline
                .as_ref()
                .unwrap()
                .conclusion
                .as_deref(),
            Some("failure")
        );
        assert!(s.implementations[0].pipeline.is_none());
        let json = serde_json::to_value(&s).unwrap();
        assert!(json["implementations"][2]["packages"][0]["publishedAt"].is_string());
    }

    #[test]
    fn failing_source_keeps_its_last_check_time() {
        let mut cache = Cache::default();
        record(&mut cache, "pypi", None);
        let first = cache.sources["pypi"].checked_at.clone();
        record(&mut cache, "pypi", Some("HTTP 503".into()));
        assert!(!cache.sources["pypi"].ok);
        assert_eq!(cache.sources["pypi"].checked_at, first);
    }
}
