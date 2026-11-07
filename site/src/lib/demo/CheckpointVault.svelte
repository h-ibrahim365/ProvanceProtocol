<script lang="ts">
  import HashChip from "../ui/HashChip.svelte";
  import type { Demo } from "./demo.svelte";

  let { demo }: { demo: Demo } = $props();
  const c = $derived(demo.checkpoint);
</script>

<aside class="vault" aria-labelledby="vault-title">
  <div class="lock" aria-hidden="true">
    <svg viewBox="0 0 24 24" width="22" height="22"><rect x="5" y="10" width="14" height="10" rx="2" fill="none" stroke="currentColor" stroke-width="1.8" /><path d="M8 10V7a4 4 0 0 1 8 0v3" fill="none" stroke="currentColor" stroke-width="1.8" /></svg>
  </div>
  <div>
    <h3 id="vault-title">Outside the database</h3>
    {#if c}
      <p>Checkpoint: the ledger was at entry <strong>{c.sequence}</strong> with hash <HashChip hash={c.hash} />, signed with <code>{c.keyId}</code>.</p>
      <p class="hint">Kept by another team, in storage with a retention lock. Changing the database does not change it.</p>
    {:else}
      <p class="hint">No checkpoint yet. Save one before attacking the end of the table.</p>
    {/if}
  </div>
</aside>

<style>
  .vault {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.8rem;
    padding: 0.9rem 1rem;
    border: 1.5px dashed var(--rule-strong);
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--sheet) 60%, transparent);
  }

  .lock {
    color: var(--ink-soft);
    padding-top: 0.1rem;
  }

  h3 {
    font-size: var(--t-s);
    margin-bottom: 0.25rem;
  }

  p {
    font-size: var(--t-s);
    line-height: 1.8;
  }

  .hint {
    color: var(--ink-soft);
    line-height: 1.5;
  }
</style>
