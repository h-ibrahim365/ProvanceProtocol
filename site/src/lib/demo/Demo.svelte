<script lang="ts">
  import CheckpointVault from "./CheckpointVault.svelte";
  import Inspector from "./Inspector.svelte";
  import LedgerTable from "./LedgerTable.svelte";
  import StepCode from "./StepCode.svelte";
  import StepNav from "./StepNav.svelte";
  import Story from "./Story.svelte";
  import { tour } from "./tour.svelte";
  import VerifyResult from "./VerifyResult.svelte";

  const demo = tour.demo;
</script>

<!-- Desktop: the step panel sits next to the table. Phone: step, then the table it acts on, then code, with the
     step navigation stuck to the bottom of the screen. -->
<div class="layout">
  <div class="left">
    <div class="head"><Story /></div>
    <div class="code"><StepCode /></div>
    <div class="nav"><StepNav /></div>
  </div>

  <div class="bench" id="bench">
    <div class="bench-head">
      <h3 class="table-title">Table <code>provance_ledger</code></h3>
      <p class="ledger">Ledger <code>{demo.ledgerId}</code>, key <code>{demo.keyId}</code></p>
    </div>
    <VerifyResult {demo} />
    {#if tour.step.focus === "checkpoint" || demo.checkpoint}
      <CheckpointVault {demo} />
    {/if}
    <LedgerTable {demo} tools={tour.step.rowTools ?? false} />
    <Inspector {demo} focus={tour.step.focus} />
  </div>
</div>

<style>
  .layout {
    display: grid;
    gap: 2rem;
    grid-template-columns: minmax(19rem, 24rem) minmax(0, 1fr);
    align-items: start;
  }

  /* no inner scroll area: the panel scrolls with the page */
  .left {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 1.3rem;
  }

  .bench {
    display: grid;
    gap: 1rem;
    min-width: 0;
    scroll-margin-top: 0.75rem;
  }

  .bench-head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.3rem 1rem;
  }

  .table-title {
    font-size: var(--t-m);
  }

  .ledger {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  @media (max-width: 999px) {
    .layout {
      display: flex;
      flex-direction: column;
      align-items: stretch;
      gap: 1.25rem;
    }

    .left {
      display: contents;
    }

    .head {
      order: 1;
    }

    .bench {
      order: 2;
    }

    .code {
      order: 3;
    }

    .nav {
      order: 4;
      position: sticky;
      bottom: 0;
      z-index: 5;
    }

    .code:empty {
      display: none;
    }
  }
</style>
