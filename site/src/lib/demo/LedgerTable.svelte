<script lang="ts">
  import { genesisHash } from "../provance/format";
  import HashChip from "../ui/HashChip.svelte";
  import type { Demo } from "./demo.svelte";
  import { CHECK_LABELS } from "./explain";

  interface Props {
    demo: Demo;
    tools: boolean;
  }

  let { demo, tools }: Props = $props();

  let editing = $state<number | null>(null);
  let draft = $state("");
  let error = $state<string | null>(null);

  const genesis = $derived(genesisHash(demo.ledgerId));
  const v = $derived(demo.verification);

  function rowState(index: number): "idle" | "pending" | "ok" | "void" | "unchecked" {
    if (!v) return "idle";
    if (index >= v.shown) return v.done ? "unchecked" : "pending";
    if (v.result.failedIndex === index) return "void";
    const report = v.result.reports.find((r) => r.index === index);
    if (!report) return "unchecked";
    return report.ok ? "ok" : "void";
  }

  function report(index: number) {
    return v?.result.reports.find((r) => r.index === index);
  }

  function startEdit(index: number) {
    const entry = demo.entries[index];
    if (!entry?.payloadJson) return;
    editing = index;
    error = null;
    draft = JSON.stringify(JSON.parse(entry.payloadJson), null, 2);
  }

  function saveEdit() {
    if (editing === null) return;
    error = demo.editPayload(editing, draft);
    if (!error) editing = null;
  }

  function time(ts: string) {
    return ts.replace("T", " ").slice(0, 19);
  }

  function compact(json: string) {
    return json.replace(/^\{|\}$/g, "").replace(/"([^"]+)":/g, "$1: ").replace(/,(?=[^,]*:)/g, ", ");
  }
</script>

<div class="table" role="list" aria-label="Ledger table">
  <div class="head" aria-hidden="true">
    <span class="c-seq">#</span>
    <span class="c-event">Event</span>
    <span class="c-payload">Payload</span>
    <span class="c-chain">previousHash, then hash</span>
    <span class="c-status">Check</span>
  </div>

  {#if demo.entries.length === 0}
    <p class="empty">The table is empty. Record an event to write the first entry.</p>
  {/if}

  {#if (demo.entries[0]?.sequence ?? 1) > 1}
    {@const first = demo.entries[0]!.sequence}
    <p class="gap" class:retained={demo.retainedFrom === first}>
      {#if demo.retainedFrom === first}
        Entries 1 to {first - 1} were removed by retention. They are kept in the archive.
      {:else}
        Entries 1 to {first - 1} are no longer in the table.
      {/if}
    </p>
  {/if}

  {#each demo.entries as entry, index (entry.id)}
    {@const state = rowState(index)}
    {@const edits = demo.edits[entry.id] ?? []}
    {@const r = report(index)}
    {@const expectedPrev = index === 0 ? genesis : demo.entries[index - 1]?.hash}
    <div class="row s-{state}" class:selected={demo.selected === index} role="listitem">
      <button class="main" onclick={() => (demo.selected = index)} aria-pressed={demo.selected === index}>
        <span class="c-seq">{entry.sequence}</span>
        <span class="c-event">
          <span class="event">{entry.eventType}</span>
          {#if entry.eventType.startsWith("provance.")}<span class="lib">written by the library</span>{/if}
          <span class="time">{time(entry.timestamp)}</span>
        </span>
        <span class="c-payload mono">
          {#if entry.payloadJson === null}
            <span class="redacted">redacted</span>
          {:else}
            {compact(entry.payloadJson)}
          {/if}
        </span>
        <span class="c-chain">
          <HashChip
            hash={entry.previousHash}
            label={entry.sequence === 1 && entry.previousHash === genesis ? "genesis" : undefined}
            title={`previousHash ${entry.previousHash}`}
          />
          <span class="arrow" aria-hidden="true">
            <svg viewBox="0 0 16 8" width="16" height="8"><path d="M0 4h13M10 1l3 3-3 3" fill="none" stroke="currentColor" stroke-width="1.4" /></svg>
          </span>
          <HashChip hash={entry.hash} title={`hash ${entry.hash}`} />
        </span>
        <span class="c-status">
          {#if state === "ok"}
            <span class="ok-mark" title="All checks passed">
              <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M3 8.5l3 3 7-7" fill="none" stroke="currentColor" stroke-width="2" /></svg>
              <span class="visually-hidden">valid</span>
            </span>
          {:else if state === "pending"}
            <span class="pending-mark" aria-label="waiting"></span>
          {:else if state === "unchecked"}
            <span class="dim">not reached</span>
          {/if}
        </span>
        {#if edits.length > 0 && !edits.every((e) => e === "redacted")}
          <span class="edited">edited in the database</span>
        {/if}
        {#if expectedPrev && entry.previousHash !== expectedPrev && !v}
          <span class="visually-hidden">The link to the previous row does not match.</span>
        {/if}
      </button>

      {#if state === "void"}
        <div class="stamp" aria-hidden="true"><span>VOID</span></div>
        {#if r}
          <ul class="checks" aria-label="Checks on this entry">
            {#each r.checks as c (c.name)}
              <li class:fail={!c.ok} class:skip={c.skipped}>{CHECK_LABELS[c.name]}</li>
            {/each}
          </ul>
        {/if}
      {/if}

      {#if tools && demo.selected === index}
        <div class="tools" aria-label="Change this row directly in the database">
          {#if editing === index}
            <label class="visually-hidden" for="payload-edit">Payload JSON</label>
            <textarea id="payload-edit" class="mono" rows="6" bind:value={draft} spellcheck="false"></textarea>
            {#if error}<p class="error" role="alert">{error}</p>{/if}
            <div class="tool-row">
              <button class="btn" onclick={saveEdit}>Save in the table</button>
              <button class="btn quiet" onclick={() => (editing = null)}>Cancel</button>
            </div>
          {:else}
            <span class="tools-label">In the database:</span>
            <button class="btn danger" onclick={() => startEdit(index)} disabled={entry.payloadJson === null}>Edit payload</button>
            <button class="btn danger" onclick={() => demo.editEventType(index, entry.eventType === "ORDER_CANCELLED" ? "ORDER_REFUNDED" : "ORDER_CANCELLED")}>Change event type</button>
            <button class="btn danger" onclick={() => demo.swap(index, index + 1)} disabled={index >= demo.entries.length - 1}>Move down</button>
            <button class="btn danger" onclick={() => demo.coverTracks(index)}>Recompute hashes</button>
            <button class="btn danger" onclick={() => demo.remove(index)}>Delete</button>
          {/if}
        </div>
      {/if}
    </div>
  {/each}
</div>

<style>
  .table {
    background: var(--sheet);
    border: 1px solid var(--rule-strong);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .head,
  .main {
    display: grid;
    grid-template-columns: 2.6rem minmax(9rem, 1fr) minmax(8rem, 1.3fr) auto 5.5rem;
    gap: 0 0.9rem;
    align-items: center;
  }

  .head {
    padding: 0.55rem 0.9rem 0.5rem 0;
    font-size: var(--t-xs);
    color: var(--ink-soft);
    font-weight: 600;
    /* the red double rule under the headings of an account book */
    border-bottom: 3px double var(--red);
  }

  .head .c-seq,
  .main .c-seq {
    text-align: right;
    padding-right: 0.6rem;
    border-right: 1px solid color-mix(in srgb, var(--red) 55%, transparent);
    align-self: stretch;
    display: flex;
    align-items: center;
    justify-content: flex-end;
  }

  .row {
    position: relative;
    border-bottom: 1px solid var(--rule);
  }

  .row:last-child {
    border-bottom: none;
  }

  .main {
    width: 100%;
    border: none;
    background: none;
    text-align: left;
    padding: 0.55rem 0.9rem 0.55rem 0;
    cursor: pointer;
    font: inherit;
    color: inherit;
    min-height: 3.4rem;
  }

  .main:hover {
    background: color-mix(in srgb, var(--ink) 4%, transparent);
  }

  .selected .main {
    background: color-mix(in srgb, var(--link) 9%, transparent);
    box-shadow: inset 3px 0 0 var(--link);
  }

  .c-seq {
    font-family: var(--mono);
    font-size: var(--t-s);
    color: var(--ink-soft);
  }

  .c-event {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .event {
    font-weight: 600;
    font-size: var(--t-s);
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .lib {
    font-size: 0.68rem;
    font-weight: 600;
    color: var(--link);
  }

  .gap {
    padding: 0.5rem 0.9rem 0.5rem 3.5rem;
    font-size: var(--t-xs);
    color: var(--red);
    border-bottom: 1px dashed var(--rule-strong);
  }

  .gap.retained {
    color: var(--ink-soft);
  }

  .time {
    font-size: var(--t-xs);
    color: var(--ink-soft);
    font-variant-numeric: tabular-nums;
  }

  .c-payload {
    font-size: 0.7rem;
    color: var(--ink-soft);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }

  .redacted {
    font-family: var(--font);
    font-style: italic;
    font-size: var(--t-xs);
  }

  .c-chain {
    display: flex;
    align-items: center;
    gap: 0.3rem;
  }

  .arrow {
    color: var(--ink-soft);
    display: inline-flex;
  }

  .c-status {
    display: flex;
    justify-content: flex-end;
    font-size: var(--t-xs);
  }

  .ok-mark {
    color: var(--seal);
    display: inline-flex;
  }

  .dim {
    color: var(--ink-soft);
  }

  .pending-mark {
    width: 0.55rem;
    height: 0.55rem;
    border-radius: 50%;
    border: 1.5px solid var(--rule-strong);
  }

  .edited {
    grid-column: 2 / -1;
    font-size: var(--t-xs);
    color: var(--red);
    font-weight: 600;
    margin-top: 0.15rem;
  }

  .row.s-ok .main {
    box-shadow: inset 3px 0 0 var(--seal);
  }

  /* tamper-evident tape: what you see when a security seal has been peeled off */
  .row.s-void {
    background: repeating-linear-gradient(-45deg, var(--red-wash) 0 9px, transparent 9px 18px);
  }

  .stamp {
    position: absolute;
    left: 44%;
    top: 0.5rem;
    transform: rotate(-8deg);
    pointer-events: none;
  }

  .stamp span {
    display: block;
    border: 2.5px solid var(--red);
    color: var(--red);
    font-weight: 800;
    letter-spacing: 0.12em;
    font-size: var(--t-l);
    padding: 0.05rem 0.5rem;
    border-radius: 3px;
    background: color-mix(in srgb, var(--sheet) 75%, transparent);
    animation: stamp 0.28s ease-out;
  }

  @keyframes stamp {
    from {
      transform: scale(1.6);
      opacity: 0;
    }
    to {
      transform: scale(1);
      opacity: 1;
    }
  }

  .checks {
    list-style: none;
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin: 0;
    /* the row above ends with 0.55rem of padding: 0.2rem more makes the gap above the badges equal to the 0.75rem below */
    padding: 0.2rem 0.9rem 0.75rem 3.5rem;
    font-size: var(--t-xs);
  }

  .checks li {
    padding: 0.1rem 0.45rem;
    border-radius: 3px;
    background: var(--seal-wash);
    color: var(--seal);
    font-weight: 600;
  }

  .checks li.skip {
    background: transparent;
    color: var(--ink-soft);
    text-decoration: line-through;
  }

  .checks li.fail {
    background: var(--red);
    color: var(--sheet);
  }

  .tools {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem 0.65rem;
    padding: 0.75rem 0.9rem 0.75rem 3.5rem;
  }

  /* under the check badges, the badges' own 0.75rem is the gap */
  .checks + .tools {
    padding-top: 0;
  }

  .tools .btn {
    font-size: var(--t-xs);
    padding: 0.35em 0.7em;
  }

  .tools-label {
    font-size: var(--t-xs);
    color: var(--ink-soft);
    margin-right: 0.25rem;
  }

  .tools textarea {
    width: 100%;
    font-size: 0.75rem;
    border: 1.5px solid var(--red);
    border-radius: var(--radius);
    background: var(--paper);
    padding: 0.6rem;
    resize: vertical;
  }

  .tool-row {
    display: flex;
    gap: 0.4rem;
  }

  .error {
    color: var(--red);
    font-size: var(--t-xs);
    width: 100%;
  }

  .empty {
    padding: 1.4rem 1rem;
    color: var(--ink-soft);
    font-size: var(--t-s);
  }

  @media (max-width: 760px) {
    .head {
      display: none;
    }

    .main {
      grid-template-columns: 2.2rem 1fr auto;
      grid-template-areas:
        "seq event status"
        "seq payload payload"
        "seq chain chain"
        "seq edited edited";
      row-gap: 0.35rem;
    }

    .main .c-seq {
      grid-area: seq;
    }

    .c-event {
      grid-area: event;
    }

    .c-payload {
      grid-area: payload;
    }

    .c-chain {
      grid-area: chain;
    }

    .c-status {
      grid-area: status;
    }

    .edited {
      grid-area: edited;
    }

    .stamp {
      right: 1rem;
      top: 1.6rem;
      transform: rotate(-8deg);
    }

    .checks,
    .tools {
      padding-left: 2.8rem;
    }
  }
</style>
