<script lang="ts">
  import { isTampering, summary } from "../provance/compare";
  import type { Demo } from "./demo.svelte";

  let { demo }: { demo: Demo } = $props();

  const c = $derived(demo.comparison);
  const r = $derived(demo.retentionReport);
  const count = $derived(c ? c.differences.filter(isTampering).length : 0);

  let box = $state<HTMLElement>();

  // The buttons are in the side panel: bring the report into view when it is off screen.
  $effect(() => {
    if ((c || r) && box) {
      const rect = box.getBoundingClientRect();
      if (rect.top < 0 || rect.bottom > innerHeight) box.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }
  });

  /** Long hex values are cut, the full value is in the tooltip. */
  function show(value: string | null): string {
    if (value === null) return "(absent)";
    return /^[0-9a-f]{40,}$/.test(value) ? `${value.slice(0, 12)}…` : value;
  }
</script>

<div class="reports" aria-live="polite" bind:this={box}>
  {#if r}
    <div class="box" class:good={r.ok} class:bad={!r.ok}>
      <p class="verdict">{r.ok ? "Retention applied" : "Retention refused"}</p>
      <p class="detail">{r.text}</p>
    </div>
  {/if}

  {#if c}
    <div class="box" class:good={c.identical} class:bad={!c.identical}>
      <p class="verdict">{c.identical ? "The two copies match" : `${count} ${count === 1 ? "difference" : "differences"} between the copies`}</p>
      <p class="meta">
        {c.compared} {c.compared === 1 ? "entry" : "entries"} compared.
        {#if c.removedByRetention > 0}{c.removedByRetention} older entries are only in the archive (retention).{/if}
        {#if c.notYetCopied > 0}{c.notYetCopied} recent entries are not copied yet.{/if}
      </p>
      {#each c.differences as d (`${d.sequence}-${d.kind}`)}
        <div class="diff">
          <p class="title">{summary(d)}</p>
          {#if d.fields.length > 0}
            <dl class="fields">
              {#each d.fields as f (f.field)}
                <div class="field">
                  <dt><code>{f.field}</code></dt>
                  <dd>
                    <span class="side">archive</span>
                    <code class="value" class:now={d.original === "primary"} title={f.archiveValue ?? ""}>{show(f.archiveValue)}</code>
                  </dd>
                  <dd>
                    <span class="side">main table</span>
                    <code class="value" class:now={d.original !== "primary"} title={f.primaryValue ?? ""}>{show(f.primaryValue)}</code>
                  </dd>
                </div>
              {/each}
            </dl>
          {/if}
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .reports {
    display: grid;
    gap: 0.8rem;
  }

  .reports:empty {
    display: none;
  }

  .box {
    border-radius: var(--radius);
    padding: 0.9rem 1rem;
    display: grid;
    gap: 0.45rem;
    border: 1.5px solid;
  }

  .bad {
    border-color: var(--red);
    background: var(--red-wash);
  }

  .good {
    border-color: var(--seal);
    background: var(--seal-wash);
  }

  .verdict {
    font-weight: 800;
    font-size: var(--t-s);
  }

  .bad .verdict {
    color: var(--red);
  }

  .good .verdict {
    color: var(--seal);
  }

  .detail,
  .meta {
    font-size: var(--t-s);
  }

  .meta {
    color: var(--ink-soft);
  }

  .diff {
    display: grid;
    gap: 0.4rem;
    padding-top: 0.5rem;
    border-top: 1px solid color-mix(in srgb, var(--red) 30%, transparent);
  }

  .title {
    font-weight: 600;
    font-size: var(--t-s);
  }

  .fields {
    display: grid;
    gap: 0.35rem;
    margin: 0;
  }

  .field {
    display: grid;
    grid-template-columns: minmax(8rem, auto) 1fr 1fr;
    gap: 0.2rem 0.8rem;
    align-items: baseline;
    padding: 0.4rem 0.6rem;
    background: var(--sheet);
    border-radius: var(--radius);
  }

  dt code {
    font-size: 0.72rem;
    font-weight: 600;
  }

  dd {
    margin: 0;
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .side {
    font-size: 0.68rem;
    color: var(--ink-soft);
  }

  .value {
    font-size: 0.72rem;
    overflow-wrap: anywhere;
  }

  .now {
    color: var(--red);
    font-weight: 600;
  }

  @media (max-width: 640px) {
    .field {
      grid-template-columns: 1fr 1fr;
    }

    dt {
      grid-column: 1 / -1;
    }
  }
</style>
