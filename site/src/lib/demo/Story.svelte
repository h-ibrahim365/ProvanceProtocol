<script lang="ts">
  import { reveal } from "../ui/media.svelte";
  import { VERSIONS } from "../versions";
  import type { Action } from "./steps";
  import { tour } from "./tour.svelte";

  const demo = tour.demo;
  const steps = $derived(tour.version.steps);
  const step = $derived(tour.step);

  function run(action: Action) {
    action.run(demo);
    // On a phone the table is below this panel: show what the tap changed.
    requestAnimationFrame(() => reveal("bench"));
  }
</script>

<div class="story" id="step-head">
  <div class="version">
    <label for="version">Version</label>
    <select id="version" value={tour.version.id} onchange={(e) => tour.chooseVersion(e.currentTarget.value)}>
      {#each VERSIONS as v (v.id)}
        <option value={v.id} disabled={!v.released}>{v.label}</option>
      {/each}
    </select>
    <details>
      <summary>What is in {tour.version.label}</summary>
      <p>{tour.version.summary}</p>
      <ul>
        {#each tour.version.changes as c (c)}<li>{c}</li>{/each}
      </ul>
      {#if VERSIONS.some((v) => !v.released)}
        <p class="next">
          Next: {VERSIONS.filter((v) => !v.released).map((v) => v.changes.join(", ").toLowerCase()).join("; ")}.
        </p>
      {/if}
    </details>
  </div>

  <nav aria-label="Steps">
    <ol class="steps">
      {#each steps as s, i (s.id)}
        <li>
          <button
            class:current={i === tour.stepIndex}
            class:past={i < tour.stepIndex}
            aria-current={i === tour.stepIndex ? "step" : undefined}
            aria-label={`Step ${i + 1}: ${s.title}`}
            onclick={() => tour.goto(i)}>
            <span class="n" aria-hidden="true">{i + 1}</span>
            <span class="t">{s.title}</span>
          </button>
        </li>
      {/each}
    </ol>
  </nav>

  <article class="current" aria-labelledby="step-title">
    <p class="count">Step {tour.stepIndex + 1} of {steps.length}</p>
    <h2 id="step-title">{step.title}</h2>
    {#each step.body as paragraph (paragraph)}<p>{paragraph}</p>{/each}
    {#if step.note}<p class="note">{step.note}</p>{/if}

    {#if step.actions.length > 0}
      <div class="actions">
        {#each step.actions as action (action.label)}
          <button class="btn {action.variant ?? ''}" disabled={action.disabled?.(demo) ?? false} onclick={() => run(action)}>
            {action.label}
          </button>
        {/each}
      </div>
    {/if}
  </article>
</div>

<style>
  .story {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 1.3rem;
    scroll-margin-top: 0.75rem;
  }

  .version {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.6rem;
    font-size: var(--t-s);
  }

  .version label {
    color: var(--ink-soft);
  }

  select {
    border: 1.5px solid var(--rule-strong);
    border-radius: var(--radius);
    background: var(--sheet);
    padding: 0.3rem 0.5rem;
    font-weight: 600;
    font-size: var(--t-s);
  }

  summary {
    cursor: pointer;
    color: var(--link);
    font-size: var(--t-s);
    padding: 0.2rem 0;
  }

  details[open] {
    flex-basis: 100%;
  }

  details p,
  details ul {
    font-size: var(--t-s);
    margin: 0.5rem 0 0;
  }

  details ul {
    padding-left: 1.1rem;
  }

  .next {
    color: var(--ink-soft);
  }

  .steps {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.1rem;
  }

  .steps button {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    width: 100%;
    border: none;
    background: none;
    padding: 0.28rem 0.4rem;
    border-radius: 4px;
    cursor: pointer;
    text-align: left;
    color: var(--ink-soft);
    font-size: var(--t-s);
  }

  .steps button:hover {
    background: color-mix(in srgb, var(--ink) 6%, transparent);
  }

  .steps .n {
    font-family: var(--mono);
    font-size: 0.7rem;
    width: 1.35rem;
    height: 1.35rem;
    display: inline-grid;
    place-items: center;
    border-radius: 50%;
    border: 1.5px solid var(--rule-strong);
    flex: none;
  }

  .steps .past .n {
    border-color: var(--seal);
    color: var(--seal);
  }

  .steps .current {
    color: var(--ink);
    font-weight: 700;
  }

  .steps .current .n {
    background: var(--ink);
    border-color: var(--ink);
    color: var(--sheet);
  }

  .current {
    display: grid;
    gap: 0.75rem;
    max-width: 62ch;
  }

  .count {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  h2 {
    font-size: var(--t-xl);
    text-wrap: balance;
  }

  .note {
    font-size: var(--t-s);
    color: var(--ink-soft);
    border-left: 3px solid var(--rule-strong);
    padding-left: 0.7rem;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    padding-top: 0.2rem;
  }

  @media (max-width: 999px) {
    .story {
      gap: 1rem;
    }

    /* the step strip scrolls sideways; each step is a round tap target */
    .steps {
      display: flex;
      gap: 0.35rem;
      overflow-x: auto;
      margin: 0 -1rem;
      padding: 0.2rem 1rem 0.4rem;
      scrollbar-width: none;
    }

    .steps::-webkit-scrollbar {
      display: none;
    }

    .steps button {
      padding: 0;
      width: auto;
    }

    .steps .n {
      width: 2.2rem;
      height: 2.2rem;
      font-size: 0.8rem;
    }

    .steps .t {
      display: none;
    }

    h2 {
      font-size: var(--t-l);
    }

    .current {
      gap: 0.6rem;
    }

    /* full-width buttons are easier to hit with a thumb */
    .actions {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
    }

    .actions :global(.btn) {
      justify-content: center;
      text-align: center;
    }
  }
</style>
