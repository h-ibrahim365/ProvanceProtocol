<script lang="ts">
  import { onMount } from "svelte";

  import { type Snapshot, ago, duration, runState } from "./status";

  let snapshot = $state<Snapshot | null>(null);
  let failed = $state(false);
  let loadedAt = $state(0);
  let now = $state(Date.now());

  const active = $derived(snapshot?.runs.filter((r) => r.status !== "completed") ?? []);

  async function load() {
    try {
      const response = await fetch("/api/status", { headers: { accept: "application/json" } });
      if (!response.ok) throw new Error(String(response.status));
      snapshot = (await response.json()) as Snapshot;
      failed = false;
      loadedAt = Date.now();
    } catch {
      failed = true;
    }
  }

  onMount(() => {
    load();
    let timer: ReturnType<typeof setTimeout>;
    const schedule = () => {
      timer = setTimeout(async () => {
        if (document.visibilityState === "visible") await load();
        schedule();
      }, snapshot?.active ? 5000 : 20000);
    };
    schedule();
    const clock = setInterval(() => (now = Date.now()), 1000);
    return () => {
      clearTimeout(timer);
      clearInterval(clock);
    };
  });

  /** Splits a package name after dots and colons so long names wrap between words, not inside them. */
  function parts(name: string): string[] {
    return name.split(/(?<=[.:])/);
  }

  const SOURCES: Record<string, string> = { github: "GitHub", nuget: "NuGet", maven: "Maven Central", pypi: "PyPI" };
  /** A source that failed once but answered within this delay is shown as retrying, not as down. */
  const STALE_AFTER = 5 * 60 * 1000;
</script>

<div class="releases">
  <header class="intro">
    <h2>Releases</h2>
    <p>
      The published version of every package and the state of the release pipelines, read live from GitHub, NuGet,
      Maven Central and PyPI.
    </p>
    {#if snapshot}
      <p class="fresh">Updated {ago(new Date(loadedAt).toISOString(), now)}</p>
    {/if}
  </header>

  {#if failed && !snapshot}
    <div class="offline" role="status">
      <p><strong>Live release data is not reachable right now.</strong> The registries have it too:</p>
      <ul>
        <li><a href="https://www.nuget.org/packages/Provance.Core">NuGet</a></li>
        <li><a href="https://central.sonatype.com/namespace/io.github.h-ibrahim365">Maven Central</a></li>
        <li><a href="https://pypi.org/project/provance/">PyPI</a></li>
        <li><a href="https://github.com/h-ibrahim365/ProvanceProtocol/actions">GitHub Actions</a></li>
      </ul>
    </div>
  {:else if !snapshot}
    <p class="loading">Loading release data</p>
  {:else}
    {#if active.length > 0}
      <div class="live" role="status">
        <span class="pulse" aria-hidden="true"></span>
        <p>
          {#each active as run, i (run.id)}{i > 0 ? ", " : ""}<a href={run.url}>{run.workflow}</a> ({run.title}) {runState(run).label.toLowerCase()}{run.startedAt ? `, started ${ago(run.startedAt, now)}` : ""}{/each}
        </p>
      </div>
    {/if}

    <section aria-labelledby="packages-title">
      <h3 id="packages-title">Published packages</h3>
      <div class="sheet">
        {#each snapshot.implementations as impl (impl.id)}
          {@const pipe = impl.pipeline ? runState(impl.pipeline) : null}
          <div class="impl">
            <div class="impl-head">
              <h4>{impl.name} <span class="registry">on {impl.registry}</span></h4>
              <div class="impl-meta">
                {#if impl.release}
                  <a href={impl.release.url}>{impl.release.tag}</a>
                {/if}
                {#if impl.pipeline && pipe}
                  <a class="badge {pipe.tone}" href={impl.pipeline.url} title="Last release pipeline run">
                    {#if pipe.tone === "running"}<span class="dot" aria-hidden="true"></span>{/if}
                    Release {pipe.label.toLowerCase()}
                  </a>
                {/if}
              </div>
            </div>
            {#if impl.packages.length === 0}
              <p class="none">{snapshot.sources[impl.registry === "NuGet" ? "nuget" : impl.registry === "PyPI" ? "pypi" : "maven"]?.ok === false ? `${impl.registry} is not answering right now.` : "Nothing published yet."}</p>
            {:else}
              <ul class="packages">
                {#each impl.packages as p (p.name)}
                  <li>
                    <a class="name" href={p.url}>{#each parts(p.name) as part, i (i)}{part}<wbr />{/each}</a>
                    <span class="version mono">{p.version}</span>
                    <span class="when">{ago(p.publishedAt, now)}</span>
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        {/each}
      </div>
    </section>

    <section aria-labelledby="runs-title">
      <h3 id="runs-title">Recent pipeline runs</h3>
      {#if snapshot.runs.length === 0}
        <p class="none">No runs to show.</p>
      {:else}
        <ol class="runs sheet">
          {#each snapshot.runs as run (run.id)}
            {@const s = runState(run)}
            <li>
              <span class="badge {s.tone}">{#if s.tone === "running"}<span class="dot" aria-hidden="true"></span>{/if}{s.label}</span>
              <span class="what">
                <a href={run.url}>{run.workflow}</a>
                <span class="title">{run.title}</span>
              </span>
              <span class="ref mono">{run.branch}</span>
              <span class="when">{ago(run.startedAt, now)}{#if duration(run, now)}, {duration(run, now)}{/if}</span>
            </li>
          {/each}
        </ol>
      {/if}
    </section>

    <footer class="sources">
      {#each Object.entries(snapshot.sources) as [name, state] (name)}
        {@const recent = !!state.checkedAt && now - Date.parse(state.checkedAt) < STALE_AFTER}
        <span class:down={!state.ok && !recent} class:retrying={!state.ok && recent}>
          {SOURCES[name] ?? name}:
          {#if state.ok}
            checked {ago(state.checkedAt, now)}
          {:else if recent}
            checked {ago(state.checkedAt, now)}, last try failed ({state.error}), retrying
          {:else}
            not answering ({state.error})
          {/if}
        </span>
      {/each}
    </footer>
  {/if}
</div>

<style>
  .releases {
    display: grid;
    gap: 2rem;
    max-width: 62rem;
  }

  .intro {
    display: grid;
    gap: 0.4rem;
    max-width: 60ch;
  }

  .intro h2 {
    font-size: var(--t-xl);
  }

  .fresh,
  .loading {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  h3 {
    font-size: var(--t-m);
    margin-bottom: 0.6rem;
  }

  .sheet {
    background: var(--sheet);
    border: 1px solid var(--rule-strong);
    border-radius: var(--radius);
  }

  .impl {
    padding: 0.9rem 1rem;
    border-bottom: 1px solid var(--rule);
  }

  .impl:last-child {
    border-bottom: none;
  }

  .impl-head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.4rem 1rem;
    margin-bottom: 0.4rem;
  }

  h4 {
    margin: 0;
    font-size: var(--t-l);
  }

  .registry {
    font-size: var(--t-s);
    font-weight: 500;
    color: var(--ink-soft);
  }

  .impl-meta {
    display: flex;
    align-items: center;
    gap: 0.8rem;
    font-size: var(--t-s);
  }

  .packages {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .packages li {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto 7rem;
    gap: 1rem;
    align-items: baseline;
    padding: 0.3rem 0;
    border-top: 1px dashed var(--rule);
    font-size: var(--t-s);
  }

  .packages .name {
    min-width: 0;
  }

  .version {
    font-weight: 600;
  }

  .when {
    color: var(--ink-soft);
    font-size: var(--t-xs);
    text-align: right;
  }

  .badge {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: var(--t-xs);
    font-weight: 700;
    padding: 0.15rem 0.5rem;
    border-radius: 3px;
    text-decoration: none;
    white-space: nowrap;
  }

  .badge.good {
    background: var(--seal-wash);
    color: var(--seal);
  }

  .badge.bad {
    background: var(--red-wash);
    color: var(--red);
  }

  .badge.running {
    background: color-mix(in srgb, var(--link) 14%, transparent);
    color: var(--link);
  }

  .badge.neutral {
    background: color-mix(in srgb, var(--ink) 8%, transparent);
    color: var(--ink-soft);
  }

  .dot,
  .pulse {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: currentColor;
    animation: pulse 1.4s ease-in-out infinite;
  }

  .pulse {
    color: var(--link);
    flex: none;
    margin-top: 0.45rem;
  }

  @keyframes pulse {
    50% {
      opacity: 0.25;
    }
  }

  .live {
    display: flex;
    gap: 0.7rem;
    padding: 0.8rem 1rem;
    border: 1.5px solid var(--link);
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--link) 8%, var(--sheet));
    font-size: var(--t-s);
  }

  .runs {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .runs li {
    display: grid;
    grid-template-columns: 6.5rem minmax(0, 1fr) auto 9rem;
    gap: 0.9rem;
    align-items: center;
    padding: 0.55rem 1rem;
    border-bottom: 1px solid var(--rule);
    font-size: var(--t-s);
  }

  .runs li:last-child {
    border-bottom: none;
  }

  .what {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .title {
    color: var(--ink-soft);
    font-size: var(--t-xs);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .ref {
    font-size: 0.68rem;
    color: var(--ink-soft);
  }

  .none {
    font-size: var(--t-s);
    color: var(--ink-soft);
  }

  .sources {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem 1.2rem;
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .sources .down {
    color: var(--red);
  }

  .sources .retrying {
    color: var(--ink-soft);
    font-style: italic;
  }

  .offline {
    padding: 1rem;
    border: 1.5px dashed var(--rule-strong);
    border-radius: var(--radius);
    font-size: var(--t-s);
  }

  .offline ul {
    display: flex;
    gap: 1rem;
    flex-wrap: wrap;
    padding: 0;
    list-style: none;
    margin: 0.5rem 0 0;
  }

  @media (max-width: 700px) {
    .runs li {
      grid-template-columns: auto minmax(0, 1fr);
      grid-template-areas:
        "badge what"
        "ref when";
      row-gap: 0.25rem;
    }

    .runs .badge {
      grid-area: badge;
    }

    .runs .what {
      grid-area: what;
    }

    .runs .ref {
      grid-area: ref;
    }

    .runs .when {
      grid-area: when;
    }

    .packages li {
      grid-template-columns: minmax(0, 1fr) auto;
    }

    .packages .when {
      grid-column: 1 / -1;
      text-align: left;
    }
  }
</style>
