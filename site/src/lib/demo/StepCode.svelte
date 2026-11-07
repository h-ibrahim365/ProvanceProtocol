<script lang="ts">
  import CodeSample from "../ui/CodeSample.svelte";
  import { media } from "../ui/media.svelte";
  import { tour } from "./tour.svelte";

  const step = $derived(tour.step);
  const use = $derived(step.id === "use");
</script>

{#if step.code && tour.version.code[step.code]}
  <details class="sample" open={media.wide || use}>
    <summary><svg class="chev" viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" stroke-width="2" /></svg>{use ? (step.code === "setup" ? "Install, then set up the ledger" : "Install, then record events") : "The same in your application"}</summary>
    <CodeSample samples={tour.version.code[step.code]!} install={use} />
  </details>
{/if}

{#if use}
  <ul class="links">
    <li><a href="https://www.nuget.org/packages/Provance.Core">NuGet</a></li>
    <li><a href="https://central.sonatype.com/artifact/io.github.h-ibrahim365/provance-core">Maven Central</a></li>
    <li><a href="https://pypi.org/project/provance/">PyPI</a></li>
    <li><a href="https://github.com/h-ibrahim365/ProvanceProtocol">Source and documentation</a></li>
    <li><a href="https://github.com/h-ibrahim365/ProvanceProtocol/blob/main/docs/SPEC.md">Format specification</a></li>
  </ul>
{/if}

<style>
  .sample {
    display: grid;
    gap: 0.5rem;
  }

  summary {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: var(--t-xs);
    color: var(--ink-soft);
    font-weight: 600;
    cursor: pointer;
    padding: 0.3rem 0;
    list-style: none;
  }

  summary::-webkit-details-marker {
    display: none;
  }

  .chev {
    flex: none;
    transition: transform 0.15s ease;
  }

  .sample[open] .chev {
    transform: rotate(90deg);
  }

  .links {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem 1rem;
    list-style: none;
    padding: 0;
    margin: 0.6rem 0 0;
    font-size: var(--t-s);
  }

  @media (max-width: 999px) {
    .sample {
      border: 1px solid var(--rule-strong);
      border-radius: var(--radius);
      background: var(--sheet);
      padding: 0.3rem 0.9rem;
    }

    .sample[open] {
      padding-bottom: 0.9rem;
    }

    summary {
      font-size: var(--t-s);
      color: var(--ink);
      padding: 0.55rem 0;
    }
  }
</style>
