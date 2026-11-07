//! Parsing the GitHub, NuGet, Maven Central and PyPI responses. Pure functions, tested with fixtures.

use serde_json::Value;

use crate::model::{Package, Release, Run};

pub const NUGET_PACKAGES: [&str; 3] = [
    "Provance.Core",
    "Provance.Storage.EntityFrameworkCore",
    "Provance.AspNetCore.Middleware",
];
pub const MAVEN_GROUP: &str = "io.github.h-ibrahim365";
pub const MAVEN_ARTIFACTS: [&str; 3] = [
    "provance-core",
    "provance-jdbc",
    "provance-spring-boot-starter",
];
pub const PYPI_PACKAGE: &str = "provance";

/// Release workflow of each implementation.
pub const RELEASE_WORKFLOWS: [(&str, &str); 3] = [
    ("dotnet", ".github/workflows/release.yml"),
    ("java", ".github/workflows/release-java.yml"),
    ("python", ".github/workflows/release-python.yml"),
];

pub fn nuget_url(id: &str) -> String {
    format!(
        "https://api.nuget.org/v3/registration5-gz-semver2/{}/index.json",
        id.to_lowercase()
    )
}

pub fn maven_url(artifact: &str) -> String {
    format!(
        "https://repo1.maven.org/maven2/{}/{artifact}/maven-metadata.xml",
        MAVEN_GROUP.replace('.', "/")
    )
}

pub fn pypi_url(name: &str) -> String {
    format!("https://pypi.org/pypi/{name}/json")
}

/// Latest listed version from a NuGet registration index. Pages without inline items are returned in `Err`
/// with their URLs, so the caller can fetch them and call [`nuget_from_pages`].
pub fn nuget_latest(id: &str, index: &Value) -> Result<Option<Package>, Vec<String>> {
    let pages = index["items"].as_array().cloned().unwrap_or_default();
    let missing: Vec<String> = pages
        .iter()
        .filter(|p| p.get("items").is_none())
        .filter_map(|p| p["@id"].as_str().map(str::to_owned))
        .collect();
    if !missing.is_empty() {
        return Err(missing);
    }
    Ok(nuget_from_pages(id, &pages))
}

pub fn nuget_from_pages(id: &str, pages: &[Value]) -> Option<Package> {
    // Leaves are sorted by version within and across pages: the last listed one is the latest.
    let leaf = pages
        .iter()
        .flat_map(|p| p["items"].as_array().cloned().unwrap_or_default())
        .rfind(|leaf| leaf["catalogEntry"]["listed"].as_bool() != Some(false))?;
    let entry = &leaf["catalogEntry"];
    let version = entry["version"].as_str()?.to_owned();
    Some(Package {
        name: id.to_owned(),
        url: format!("https://www.nuget.org/packages/{id}/{version}"),
        published_at: entry["published"].as_str().map(str::to_owned),
        version,
    })
}

/// `<release>` and `<lastUpdated>` (yyyyMMddHHmmss, UTC) of a `maven-metadata.xml`.
pub fn maven_latest(artifact: &str, xml: &str) -> Option<Package> {
    let version = tag(xml, "release")
        .or_else(|| tag(xml, "latest"))?
        .to_owned();
    let published_at = tag(xml, "lastUpdated").and_then(maven_time);
    Some(Package {
        name: format!("{MAVEN_GROUP}:{artifact}"),
        url: format!("https://central.sonatype.com/artifact/{MAVEN_GROUP}/{artifact}/{version}"),
        published_at,
        version,
    })
}

fn tag<'a>(xml: &'a str, name: &str) -> Option<&'a str> {
    let open = format!("<{name}>");
    let start = xml.find(&open)? + open.len();
    let end = xml[start..].find(&format!("</{name}>"))? + start;
    Some(xml[start..end].trim())
}

fn maven_time(value: &str) -> Option<String> {
    if value.len() != 14 || !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let (d, t) = value.split_at(8);
    Some(format!(
        "{}-{}-{}T{}:{}:{}Z",
        &d[..4],
        &d[4..6],
        &d[6..],
        &t[..2],
        &t[2..4],
        &t[4..]
    ))
}

pub fn pypi_latest(json: &Value) -> Option<Package> {
    let name = json["info"]["name"].as_str()?.to_owned();
    let version = json["info"]["version"].as_str()?.to_owned();
    let published_at = json["releases"][&version]
        .as_array()
        .and_then(|files| {
            files
                .iter()
                .filter_map(|f| f["upload_time_iso_8601"].as_str())
                .min()
        })
        .map(str::to_owned);
    Some(Package {
        url: format!("https://pypi.org/project/{name}/{version}/"),
        name,
        version,
        published_at,
    })
}

/// Workflow runs, newest first. Runs whose jobs were all skipped (release workflows of the other languages)
/// are left out.
pub fn runs(json: &Value) -> Vec<Run> {
    json["workflow_runs"]
        .as_array()
        .map(|runs| {
            runs.iter()
                .filter_map(|r| {
                    Some(Run {
                        id: r["id"].as_u64()?,
                        workflow: r["name"].as_str().unwrap_or_default().to_owned(),
                        path: r["path"].as_str().unwrap_or_default().to_owned(),
                        title: r["display_title"].as_str().unwrap_or_default().to_owned(),
                        status: r["status"].as_str()?.to_owned(),
                        conclusion: r["conclusion"].as_str().map(str::to_owned),
                        branch: r["head_branch"].as_str().unwrap_or_default().to_owned(),
                        event: r["event"].as_str().unwrap_or_default().to_owned(),
                        run_number: r["run_number"].as_u64().unwrap_or_default(),
                        started_at: r["run_started_at"].as_str().map(str::to_owned),
                        updated_at: r["updated_at"].as_str().map(str::to_owned),
                        url: r["html_url"].as_str()?.to_owned(),
                    })
                })
                .filter(|r| r.conclusion.as_deref() != Some("skipped"))
                .collect()
        })
        .unwrap_or_default()
}

pub fn releases(json: &Value) -> Vec<Release> {
    json.as_array()
        .map(|items| {
            items
                .iter()
                .filter(|r| r["draft"].as_bool() != Some(true))
                .filter_map(|r| {
                    let tag = r["tag_name"].as_str()?.to_owned();
                    Some(Release {
                        language: language_of_tag(&tag)?.to_owned(),
                        name: r["name"]
                            .as_str()
                            .filter(|n| !n.is_empty())
                            .unwrap_or(&tag)
                            .to_owned(),
                        published_at: r["published_at"].as_str().map(str::to_owned),
                        url: r["html_url"].as_str()?.to_owned(),
                        tag,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

pub fn language_of_tag(tag: &str) -> Option<&'static str> {
    if tag.starts_with("java-v") {
        Some("java")
    } else if tag.starts_with("python-v") {
        Some("python")
    } else if tag.starts_with("dotnet-v") || tag.starts_with('v') {
        Some("dotnet")
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> String {
        std::fs::read_to_string(format!(
            "{}/tests/fixtures/{name}",
            env!("CARGO_MANIFEST_DIR")
        ))
        .unwrap()
    }

    fn json(name: &str) -> Value {
        serde_json::from_str(&fixture(name)).unwrap()
    }

    #[test]
    fn pypi() {
        let p = pypi_latest(&json("pypi.json")).unwrap();
        assert_eq!(p.name, "provance");
        assert_eq!(p.version, "0.1.0");
        assert!(p.published_at.unwrap().starts_with("2026-11-06T"));
        assert_eq!(p.url, "https://pypi.org/project/provance/0.1.0/");
    }

    #[test]
    fn nuget_inline_pages_skip_unlisted() {
        let p = nuget_latest("Provance.Core", &json("nuget-registration.json"))
            .unwrap()
            .unwrap();
        assert_eq!(p.version, "0.1.0");
        assert_eq!(
            p.published_at.as_deref(),
            Some("2026-11-06T14:02:11.297+00:00")
        );
        assert_eq!(p.url, "https://www.nuget.org/packages/Provance.Core/0.1.0");
    }

    #[test]
    fn nuget_external_pages_are_requested() {
        let index = serde_json::json!({ "items": [{ "@id": "https://api.nuget.org/page/1.json", "count": 200 }] });
        assert_eq!(
            nuget_latest("X", &index),
            Err(vec!["https://api.nuget.org/page/1.json".to_owned()])
        );
    }

    #[test]
    fn maven() {
        let p = maven_latest("provance-core", &fixture("maven-metadata.xml")).unwrap();
        assert_eq!(p.version, "0.1.0");
        assert_eq!(p.published_at.as_deref(), Some("2026-11-06T18:41:07Z"));
        assert_eq!(p.name, "io.github.h-ibrahim365:provance-core");
        assert!(maven_latest("x", "<metadata/>").is_none());
    }

    #[test]
    fn github_runs_leave_out_skipped() {
        let r = runs(&json("github-runs.json"));
        assert_eq!(r.len(), 3);
        assert_eq!(r[0].workflow, "Release (Python)");
        assert!(r[0].is_active());
        assert_eq!(r[1].conclusion.as_deref(), Some("success"));
        assert!(
            r.iter()
                .all(|run| run.conclusion.as_deref() != Some("skipped"))
        );
    }

    #[test]
    fn github_releases_by_language() {
        let r = releases(&json("github-releases.json"));
        let langs: Vec<_> = r
            .iter()
            .map(|x| (x.tag.as_str(), x.language.as_str()))
            .collect();
        assert_eq!(
            langs,
            [
                ("python-v0.1.0", "python"),
                ("java-v0.1.0", "java"),
                ("v0.1.0", "dotnet")
            ]
        );
        assert_eq!(language_of_tag("dotnet-v0.2.0"), Some("dotnet"));
        assert_eq!(language_of_tag("nightly"), None);
    }
}
