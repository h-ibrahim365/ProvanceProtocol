<script lang="ts">
  import { media } from "../ui/media.svelte";
  import { tour } from "./tour.svelte";

  const steps = $derived(tour.version.steps);
  const next = $derived(steps[tour.stepIndex + 1]);

  function go(index: number) {
    tour.goto(index);
    if (!media.wide) {
      const head = document.getElementById("step-head");
      const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
      head?.scrollIntoView({ behavior: reduced ? "auto" : "smooth", block: "start" });
    }
  }
</script>

<nav class="stepnav" aria-label="Previous and next step">
  <button class="btn quiet prev" onclick={() => go(tour.stepIndex - 1)} disabled={tour.stepIndex === 0}>
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M10 3L5 8l5 5" fill="none" stroke="currentColor" stroke-width="2" /></svg>
    <span class="label">Previous</span>
  </button>
  <span class="where" aria-live="polite">{tour.stepIndex + 1} / {steps.length}</span>
  {#if next}
    <button class="btn next" onclick={() => go(tour.stepIndex + 1)}>
      <span class="label">Next: {next.title.toLowerCase()}</span>
      <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" stroke-width="2" /></svg>
    </button>
  {:else}
    <button class="btn quiet next" onclick={() => { tour.demo.reset(); go(0); }}>Start over</button>
  {/if}
</nav>

<style>
  .stepnav {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding-top: 0.8rem;
    border-top: 1px solid var(--rule);
  }

  .where {
    display: none;
  }

  .next {
    margin-left: auto;
    min-width: 0;
  }

  .label {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* On phones: a bar stuck to the bottom of the screen while the demo is in view. */
  @media (max-width: 999px) {
    .stepnav {
      margin: 0 calc(-1 * max(1rem, env(safe-area-inset-left, 0px)));
      padding: 0.6rem max(1rem, env(safe-area-inset-right, 0px)) calc(0.6rem + env(safe-area-inset-bottom, 0px))
        max(1rem, env(safe-area-inset-left, 0px));
      background: color-mix(in srgb, var(--paper) 92%, transparent);
      backdrop-filter: blur(8px);
      border-top: 1px solid var(--rule-strong);
    }

    .prev .label {
      display: none;
    }

    .prev {
      flex: none;
      padding: 0.5em 0.7em;
    }

    .where {
      display: block;
      font-family: var(--mono);
      font-size: 0.75rem;
      color: var(--ink-soft);
      flex: none;
    }

    .next {
      flex: 1 1 auto;
      justify-content: space-between;
    }
  }
</style>
