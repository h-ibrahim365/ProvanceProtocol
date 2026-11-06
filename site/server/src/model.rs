//! What `/api/status` returns. Field names are camelCase for the Svelte site.

use std::collections::BTreeMap;

use serde::Serialize;

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Package {
    pub name: String,
    pub version: String,
    /// RFC 3339, when the registry gives it.
    pub published_at: Option<String>,
    pub url: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Release {
    pub tag: String,
    pub name: String,
    pub published_at: Option<String>,
    pub url: String,
    /// `dotnet`, `java` or `python`, from the tag prefix.
    pub language: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Run {
    pub id: u64,
    pub workflow: String,
    /// Workflow file, for example `.github/workflows/release.yml`.
    pub path: String,
    pub title: String,
    /// `queued`, `in_progress`, `completed`, ...
    pub status: String,
    /// `success`, `failure`, `cancelled`, `skipped`, ... once completed.
    pub conclusion: Option<String>,
    pub branch: String,
    pub event: String,
    pub run_number: u64,
    pub started_at: Option<String>,
    pub updated_at: Option<String>,
    pub url: String,
}

impl Run {
    pub fn is_active(&self) -> bool {
        self.status != "completed"
    }
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Implementation {
    pub id: &'static str,
    pub name: &'static str,
    pub registry: &'static str,
    pub packages: Vec<Package>,
    /// Latest GitHub release of this implementation.
    pub release: Option<Release>,
    /// Latest run of its release workflow that was not skipped.
    pub pipeline: Option<Run>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SourceState {
    pub ok: bool,
    pub checked_at: Option<String>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub generated_at: String,
    pub repository: String,
    /// True while a workflow run is queued or in progress.
    pub active: bool,
    pub implementations: Vec<Implementation>,
    /// Recent workflow runs, newest first, skipped runs left out.
    pub runs: Vec<Run>,
    pub sources: BTreeMap<&'static str, SourceState>,
}
