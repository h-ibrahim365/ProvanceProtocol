<script lang="ts">
  import HashChip from "../ui/HashChip.svelte";
  import type { Demo } from "./demo.svelte";

  let { demo }: { demo: Demo } = $props();

  const rows = $derived(demo.archive ?? []);
  const firstInMain = $derived(demo.entries[0]?.sequence ?? 1);

  function compact(json: string) {
    return json.replace(/^\{|\}$/g, "").replace(/"([^"]+)":/g, "$1: ").replace(/,(?=[^,]*:)/g, ", ");
  }
</script>

<section class="archive" aria-labelledby="archive-title">
  <div class="head">
    <h3 id="archive-title">Table <code>provance_archive</code></h3>
    <p class="where">Another database. The application can only insert there.</p>
  </div>

  <div class="table" role="list" aria-label="Archive table">
    {#if rows.length === 0}
      <p class="empty">Nothing copied yet.</p>
    {/if}
    {#each rows as entry (entry.id)}
      {@const changed = !!demo.archiveEdits[entry.id]}
      {@const onlyHere = entry.sequence < firstInMain && demo.retainedFrom > 1}
      <div class="row" class:changed class:only={onlyHere} role="listitem">
        <span class="seq">{entry.sequence}</span>
        <span class="event">{entry.eventType}</span>
        <span class="payload mono">
          {#if entry.payloadJson === null}<span class="redacted">redacted</span>{:else}{compact(entry.payloadJson)}{/if}
        </span>
        <HashChip hash={entry.hash} title={`hash ${entry.hash}`} />
        {#if changed}
          <span class="tag bad">edited in the archive</span>
        {:else if onlyHere}
          <span class="tag">only in the archive</span>
        {/if}
      </div>
    {/each}
  </div>

  <p class="status" class:problem={!!demo.archiveProblem}>
    {#if demo.archiveProblem}
      Copy stopped: {demo.archiveProblem}
    {:else if rows.length > 0}
      Copied through entry {rows.at(-1)?.sequence}, each entry checked before it was copied.
    {/if}
  </p>
</section>

<style>
  .archive {
    display: grid;
    gap: 0.5rem;
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.3rem 1rem;
  }

  h3 {
    font-size: var(--t-m);
  }

  .where,
  .status {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .status.problem {
    color: var(--red);
    font-weight: 600;
  }

  .table {
    background: color-mix(in srgb, var(--sheet) 70%, transparent);
    border: 1.5px dashed var(--rule-strong);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .row {
    display: grid;
    grid-template-columns: 2.2rem minmax(7rem, 0.8fr) minmax(6rem, 1.3fr) auto;
    gap: 0 0.8rem;
    align-items: center;
    padding: 0.4rem 0.8rem 0.4rem 0;
    border-bottom: 1px solid var(--rule);
    font-size: var(--t-xs);
  }

  .row:last-child {
    border-bottom: none;
  }

  .seq {
    text-align: right;
    font-family: var(--mono);
    color: var(--ink-soft);
  }

  .event {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .payload {
    font-size: 0.68rem;
    color: var(--ink-soft);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }

  .redacted {
    font-style: italic;
    font-family: var(--font);
  }

  .tag {
    grid-column: 2 / -1;
    justify-self: start;
    font-size: 0.68rem;
    font-weight: 600;
    color: var(--ink-soft);
  }

  .tag.bad {
    color: var(--red);
  }

  .changed {
    background: var(--red-wash);
  }

  .only {
    background: color-mix(in srgb, var(--link) 7%, transparent);
  }

  .empty {
    padding: 0.8rem;
    font-size: var(--t-s);
    color: var(--ink-soft);
  }

  @media (max-width: 640px) {
    .row {
      grid-template-columns: 2rem minmax(0, 1fr) auto;
    }

    .payload {
      grid-column: 2 / -1;
      grid-row: 2;
    }
  }
</style>
