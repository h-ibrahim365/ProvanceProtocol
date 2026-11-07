<script lang="ts">
  import type { Demo } from "./demo.svelte";
  import { explainFailure, hiddenTruncation } from "./explain";

  let { demo }: { demo: Demo } = $props();

  const v = $derived(demo.verification);
  const failure = $derived(v?.done && v.result.failure ? explainFailure(v.result.failure, v.result.failedAtSequence) : null);
  /** Failures found after reading every entry: an erasure without its record, or a start without a retention record. */
  const wholeTable = $derived(v?.result.failure === "unrecorded_redaction"
    || (v?.result.failure === "sequence_gap" && v.result.failedAtSequence === 1));
  const hidden = $derived(v?.done && v.result.valid && !v.withCheckpoint ? hiddenTruncation(demo) : []);

  let box = $state<HTMLElement>();

  // The buttons are in the side panel: bring the verdict into view when it is off screen.
  $effect(() => {
    if (v?.done && box) {
      const r = box.getBoundingClientRect();
      if (r.top < 0 || r.bottom > innerHeight) box.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }
  });
</script>

<div class="result" aria-live="polite" bind:this={box}>
  {#if !v}
    <!-- nothing yet -->
  {:else if !v.done}
    <p class="running">Checking entry {Math.min(v.shown + 1, demo.entries.length)} of {demo.entries.length}</p>
  {:else if failure}
    <div class="box bad">
      <p class="verdict">Tampering detected</p>
      <p class="title">{failure.title}</p>
      <p class="detail">{failure.detail}</p>
      <p class="meta">
        {#if wholeTable}
          Each entry checks out on its own: this only shows once the whole table has been read.
        {:else}
          {v.result.entriesVerified} {v.result.entriesVerified === 1 ? "entry" : "entries"} verified before it.
        {/if}
        {#if v.withCheckpoint}Checked against the checkpoint.{/if}
      </p>
    </div>
  {:else}
    <div class="box good" class:warn={hidden.length > 0}>
      <p class="verdict">Valid</p>
      <p class="title">{v.result.message}</p>
      {#if v.result.redacted > 0}
        <p class="detail">
          {v.result.redacted} erased {v.result.redacted === 1 ? "payload" : "payloads"}: the entry and its hash remain, so the chain still holds.
          {#if demo.recordsRedactions}Each erasure is named by a signed redaction record.{/if}
        </p>
      {/if}
      {#if hidden.length > 0}
        <p class="detail">
          Yet you deleted entry {hidden.join(" and ")} at the end. Nothing left in the table shows it. Only a checkpoint
          kept elsewhere can.
        </p>
      {:else if !v.withCheckpoint}
        <p class="meta">Without a checkpoint, entries removed from the end would go unnoticed.</p>
      {:else}
        <p class="meta">The ledger contains the entry recorded in the checkpoint, with the same hash.</p>
      {/if}
    </div>
  {/if}
</div>

<style>
  .running {
    font-size: var(--t-s);
    color: var(--ink-soft);
  }

  .box {
    border-radius: var(--radius);
    padding: 0.9rem 1rem;
    display: grid;
    gap: 0.3rem;
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

  .good.warn {
    border-color: var(--red);
    background: var(--sheet);
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

  .title {
    font-weight: 600;
  }

  .detail,
  .meta {
    font-size: var(--t-s);
  }

  .meta {
    color: var(--ink-soft);
  }
</style>
