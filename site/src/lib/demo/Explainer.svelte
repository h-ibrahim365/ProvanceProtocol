<script lang="ts">
  import type { Demo } from "./demo.svelte";
  import { tour } from "./tour.svelte";

  type Outcome = "stops" | "fooled" | null;

  interface Attack {
    what: string;
    note: string;
    chain: Outcome;
    signature: Outcome;
    checkpoint: Outcome;
    run?: (demo: Demo) => void;
  }

  const fresh = (step: string, count: number) => {
    tour.gotoId(step);
    tour.demo.restore();
    tour.demo.ensure(count);
  };

  const ATTACKS: Attack[] = [
    {
      what: "Change a value in an entry",
      note: "The stored payload no longer matches the payloadHash the entry was written with.",
      chain: "stops", signature: null, checkpoint: null,
      run: (d) => {
        fresh("verify", 4);
        d.changeAmount(0);
        d.verify();
      },
    },
    {
      what: "Delete or move an entry in the middle",
      note: "The sequence jumps, and the next entry points to a hash that is no longer there.",
      chain: "stops", signature: null, checkpoint: null,
      run: (d) => {
        fresh("verify", 4);
        d.remove(1);
        d.verify();
      },
    },
    {
      what: "Change a value, then recompute every hash after it",
      note: "Every link matches again: anyone can compute SHA-256. But the signatures were made with the key, and they no longer match the new hashes.",
      chain: "fooled", signature: "stops", checkpoint: null,
      run: (d) => {
        fresh("verify", 4);
        d.changeAmount(0);
        d.rechainWithoutKey(0);
        d.verify();
      },
    },
    {
      what: "Delete the last entries",
      note: "What is left is a shorter chain, valid in every way. Only a record kept outside the database knows it used to be longer.",
      chain: "fooled", signature: "fooled", checkpoint: "stops",
      run: (d) => {
        fresh("checkpoint", 5);
        d.checkpoint = null;
        d.saveCheckpoint();
        d.removeLast(2);
        d.verify(true);
      },
    },
    {
      what: "Rewrite everything with the signing key",
      note: "Whoever holds the key can produce a new chain with valid signatures. The checkpoint still holds the old hash of that entry.",
      chain: "fooled", signature: "fooled", checkpoint: "stops",
      run: (d) => {
        fresh("checkpoint", 5);
        d.checkpoint = null;
        d.saveCheckpoint();
        d.rewriteWithKey();
        d.verify(true);
      },
    },
    {
      what: "Rewrite everything with the key, and replace the checkpoint too",
      note: "This is the limit. Keep the key in a secret store or a KMS, and the checkpoint with a team or service the key holders cannot write to.",
      chain: "fooled", signature: "fooled", checkpoint: "fooled",
    },
  ];

  const LAYERS = [
    { key: "chain", name: "Hash chain", text: "Each entry includes the hash of the entry before it." },
    { key: "signature", name: "Signature", text: "Each hash is signed with a secret key." },
    { key: "checkpoint", name: "Checkpoint", text: "A signed note of the last entry, kept outside the database." },
  ] as const;

  function play(attack: Attack) {
    attack.run?.(tour.demo);
    document.getElementById("try")?.scrollIntoView({ behavior: "smooth", block: "start" });
  }
</script>

<section class="explainer" aria-labelledby="explainer-title">
  <div class="intro">
    <h2 id="explainer-title">Why tampering shows</h2>
    <p>
      Anyone with access to the database can change it. PROVANCE does not prevent that: it makes every change visible.
      Three layers do the work, and each one exists because the one before it can be fooled.
    </p>
  </div>

  <p class="legend" aria-hidden="true">
    <span class="chip stops"><svg viewBox="0 0 16 16" width="13" height="13"><path d="M3 8.5l3 3 7-7" fill="none" stroke="currentColor" stroke-width="2.4" /></svg>catches it</span>
    <span class="chip fooled"><svg viewBox="0 0 16 16" width="11" height="11"><path d="M4 4l8 8M12 4l-8 8" fill="none" stroke="currentColor" stroke-width="2.2" /></svg>fooled</span>
    <span class="chip none-l">not reached</span>
  </p>

  <div class="matrix" role="table" aria-label="Attacks and the layer that catches them">
    <div class="row head" role="row">
      <span class="attack" role="columnheader">If someone with database access tries to</span>
      {#each LAYERS as layer, i (layer.key)}
        <span class="layer" role="columnheader">
          <span class="layer-name"><span class="n">{i + 1}</span>{layer.name}</span>
          <span class="layer-text">{layer.text}</span>
        </span>
      {/each}
      <span class="try-col" aria-hidden="true"></span>
    </div>

    {#each ATTACKS as attack (attack.what)}
      {@const undetected = !LAYERS.some((l) => attack[l.key] === "stops")}
      <div class="row" class:limit={undetected} role="row">
        <div class="attack" role="cell">
          <p class="what">{attack.what}</p>
          <p class="note">{attack.note}</p>
        </div>
        <div class="cells" role="none">
          {#each LAYERS as layer (layer.key)}
            {@const outcome = attack[layer.key]}
            <div class="cell" role="cell">
              <span class="chip {outcome ?? 'none'}">
                {#if outcome === "stops"}
                  <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true"><path d="M3 8.5l3 3 7-7" fill="none" stroke="currentColor" stroke-width="2.4" /></svg>
                {:else if outcome === "fooled"}
                  <svg viewBox="0 0 16 16" width="11" height="11" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" fill="none" stroke="currentColor" stroke-width="2.2" /></svg>
                {/if}
                <span class="lname">{layer.name}</span>
                <span class="state">{outcome === "stops" ? "Catches it" : outcome === "fooled" ? "Fooled" : "Not reached"}</span>
              </span>
            </div>
          {/each}
        </div>
        <div class="try-col" role="cell">
          {#if attack.run}
            <button class="btn quiet" onclick={() => play(attack)}>Try it<span class="long">&nbsp;in the demo</span></button>
          {:else}
            <span class="undetected">Not detected</span>
          {/if}
        </div>
      </div>
    {/each}
  </div>

  <p class="foot">
    Verification reads the table from the first entry to the last, checking each one against the one before it,
    and stops at the first entry that fails.
  </p>
</section>

<style>
  .explainer {
    display: grid;
    gap: 1.2rem;
  }

  .intro {
    display: grid;
    gap: 0.5rem;
    max-width: 64ch;
  }

  h2 {
    font-size: var(--t-xl);
  }

  .intro p {
    color: var(--ink-soft);
  }

  .matrix {
    background: var(--sheet);
    border: 1px solid var(--rule-strong);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .row {
    display: grid;
    grid-template-columns: minmax(16rem, 2.2fr) repeat(3, minmax(7rem, 1fr)) 11rem;
    gap: 0 1.2rem;
    align-items: center;
    padding: 0.8rem 1rem;
    border-bottom: 1px solid var(--rule);
  }

  .row:last-child {
    border-bottom: none;
  }

  .head {
    align-items: end;
    border-bottom: 3px double var(--red);
    padding-top: 0.9rem;
  }

  .head .attack {
    font-size: var(--t-xs);
    color: var(--ink-soft);
    font-weight: 600;
  }

  .layer {
    display: grid;
    gap: 0.2rem;
  }

  .layer-name {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    font-weight: 700;
    font-size: var(--t-s);
  }

  .n {
    font-family: var(--mono);
    font-size: 0.65rem;
    width: 1.2rem;
    height: 1.2rem;
    display: inline-grid;
    place-items: center;
    border-radius: 50%;
    background: var(--ink);
    color: var(--sheet);
  }

  .layer-text {
    font-size: var(--t-xs);
    color: var(--ink-soft);
    line-height: 1.4;
  }

  .what {
    font-weight: 600;
    font-size: var(--t-s);
  }

  .note {
    font-size: var(--t-xs);
    color: var(--ink-soft);
    margin-top: 0.15rem;
    max-width: 52ch;
  }

  .cells {
    display: contents;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    font-size: var(--t-xs);
    font-weight: 700;
    padding: 0.2rem 0.5rem;
    border-radius: 3px;
    white-space: nowrap;
    border: 1px solid transparent;
  }

  .chip.stops {
    background: var(--seal-wash);
    color: var(--seal);
  }

  .chip.fooled {
    color: var(--red);
    border: 1px dashed color-mix(in srgb, var(--red) 60%, transparent);
  }

  /* on wide screens the column says which layer it is: show the state only */
  .lname {
    display: none;
  }

  .chip.none {
    padding: 0;
  }

  .chip.none .state {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  .chip.none::before {
    content: "";
    width: 1rem;
    border-top: 1px solid var(--rule-strong);
  }

  .try-col {
    display: flex;
    justify-content: flex-end;
  }

  .try-col .btn {
    font-size: var(--t-xs);
    padding: 0.4em 0.75em;
  }

  .limit {
    background: repeating-linear-gradient(-45deg, var(--red-wash) 0 9px, transparent 9px 18px);
  }

  .undetected {
    font-size: var(--t-xs);
    font-weight: 800;
    color: var(--red);
  }

  .legend {
    display: none;
  }

  .foot {
    font-size: var(--t-s);
    color: var(--ink-soft);
    max-width: 70ch;
  }

  @media (max-width: 900px) {
    .head {
      display: none;
    }

    .legend {
      display: flex;
      flex-wrap: wrap;
      gap: 0.4rem;
      font-size: var(--t-xs);
    }

    .legend .none-l {
      color: var(--ink-soft);
      font-weight: 500;
      border: 1px solid var(--rule);
    }

    .row {
      grid-template-columns: minmax(0, 1fr) auto;
      grid-template-areas:
        "attack try"
        "cells cells";
      gap: 0.6rem 0.8rem;
      align-items: start;
      padding: 0.9rem 0.9rem 1rem;
    }

    .attack {
      grid-area: attack;
    }

    .try-col {
      grid-area: try;
    }

    .cells {
      grid-area: cells;
      display: flex;
      flex-wrap: wrap;
      gap: 0.35rem;
    }

    /* on phones each chip names its layer, and its color says what happened */
    .lname {
      display: inline;
    }

    .state {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }

    .chip {
      padding: 0.3rem 0.55rem;
    }

    .chip.none {
      padding: 0.3rem 0.55rem;
      color: var(--ink-soft);
      font-weight: 500;
      border: 1px solid var(--rule);
    }

    .chip.none::before {
      display: none;
    }

    .long {
      display: none;
    }

    .try-col .btn {
      padding: 0.45em 0.8em;
    }

    .what {
      font-size: var(--t-m);
    }

    .note {
      font-size: var(--t-s);
      margin-top: 0.25rem;
    }

    .undetected {
      padding-top: 0.2rem;
    }
  }
</style>
