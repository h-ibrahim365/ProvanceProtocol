<script lang="ts">
  import { HmacKeys, canonicalEnvelope, genesisHash, sha256Hex } from "../provance/format";
  import HashChip from "../ui/HashChip.svelte";
  import { media } from "../ui/media.svelte";
  import type { Demo } from "./demo.svelte";
  import type { Focus } from "./steps";

  interface Props {
    demo: Demo;
    focus: Focus;
  }

  let { demo, focus }: Props = $props();

  const keys = $derived(new HmacKeys({ [demo.keyId]: demo.secret }));
  const entry = $derived(demo.entries[demo.selected] ?? null);
  const previous = $derived(demo.selected > 0 ? (demo.entries[demo.selected - 1] ?? null) : null);

  const computed = $derived.by(() => {
    if (!entry) return null;
    const payloadHash = entry.payloadJson === null ? null : sha256Hex(entry.payloadJson);
    const envelope = canonicalEnvelope(entry);
    const hash = sha256Hex(envelope);
    const expectedPrevious = previous ? previous.hash : genesisHash(demo.ledgerId);
    return {
      payloadHash,
      envelope,
      hash,
      signature: keys.sign(entry.keyId, entry.hash),
      expectedPrevious,
    };
  });

  /** The canonical envelope with the two hashes it commits to marked. */
  function parts(envelope: string) {
    return envelope.split(/("payloadHash":"[0-9a-f]+"|"previousHash":"[0-9a-f]+")/);
  }
</script>

<details class="inspector" open={media.wide}>
  <summary>
    <svg class="chev" viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M6 3l5 5-5 5" fill="none" stroke="currentColor" stroke-width="2" /></svg>
    <span class="title">{entry ? `How entry ${entry.sequence} is sealed` : "How an entry is sealed"}</span>
    <span class="hint">payload, hash and signature, computed live</span>
  </summary>
  {#if !entry || !computed}
    <p class="empty">Select a row to see how its hashes are computed.</p>
  {:else}

    <ol class="pipeline">
      <li class:focus={focus === "payload" || focus === "redact"}>
        <span class="what">Payload, as canonical JSON</span>
        {#if entry.payloadJson === null}
          <p class="note">Erased. The entry keeps its payloadHash, so the chain does not change.</p>
        {:else}
          <code class="block">{entry.payloadJson}</code>
          <span class="op">SHA-256</span>
          <span class="result">
            {#if computed.payloadHash === entry.payloadHash}
              <HashChip hash={entry.payloadHash} label="payloadHash" />
            {:else}
              <HashChip hash={computed.payloadHash ?? ""} label="now" bad />
              <span class="vs">but the entry says</span>
              <HashChip hash={entry.payloadHash} label="payloadHash" />
            {/if}
          </span>
        {/if}
      </li>

      <li class:focus={focus === "link"}>
        <span class="what">Link to the {entry.sequence === 1 ? "genesis hash" : `hash of entry ${entry.sequence - 1}`}</span>
        <span class="result">
          {#if entry.previousHash === computed.expectedPrevious}
            <HashChip hash={entry.previousHash} label="previousHash" />
            <span class="vs">matches</span>
          {:else}
            <HashChip hash={entry.previousHash} label="previousHash" bad />
            <span class="vs">but the row before has</span>
            <HashChip hash={computed.expectedPrevious} />
          {/if}
        </span>
      </li>

      <li class:focus={focus === "seal"}>
        <span class="what">Envelope: every field except the payload</span>
        <code class="block">{#each parts(computed.envelope) as part}{#if part.startsWith('"payloadHash"') || part.startsWith('"previousHash"')}<mark>{part}</mark>{:else}{part}{/if}{/each}</code>
        <span class="op">SHA-256</span>
        <span class="result">
          {#if computed.hash === entry.hash}
            <HashChip hash={entry.hash} label="hash" />
          {:else}
            <HashChip hash={computed.hash} label="now" bad />
            <span class="vs">but the entry says</span>
            <HashChip hash={entry.hash} label="hash" />
          {/if}
        </span>
      </li>

      <li class:focus={focus === "seal"}>
        <span class="what">Signature of the hash with key <code>{entry.keyId}</code></span>
        <span class="op">HMAC-SHA256</span>
        <span class="result">
          {#if computed.signature === entry.signature}
            <HashChip hash={entry.signature} label="signature" />
          {:else}
            <HashChip hash={entry.signature} label="signature" bad />
            <span class="vs">only the key holder could make</span>
            <HashChip hash={computed.signature} />
          {/if}
        </span>
        {#if focus === "seal"}
          <p class="note">Demo key: <code>{demo.secret}</code></p>
        {/if}
      </li>
    </ol>
  {/if}
</details>

<style>
  .inspector {
    background: var(--sheet);
    border: 1px solid var(--rule-strong);
    border-radius: var(--radius);
    padding: 1rem 1.1rem 1.1rem;
  }

  summary {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.2rem 0.6rem;
    cursor: pointer;
    list-style: none;
  }

  summary::-webkit-details-marker {
    display: none;
  }

  .chev {
    flex: none;
    color: var(--ink-soft);
    transition: transform 0.15s ease;
  }

  .inspector[open] .chev {
    transform: rotate(90deg);
  }

  .inspector[open] summary {
    margin-bottom: 0.8rem;
  }

  .title {
    font-weight: 700;
    font-size: var(--t-m);
  }

  .hint {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  @media (max-width: 999px) {
    .inspector {
      padding: 0.75rem 0.9rem;
    }

    .inspector .pipeline li {
      padding: 0.5rem 0.4rem 0.5rem 0.6rem;
    }

    .inspector .block {
      font-size: 0.66rem;
    }
  }

  .pipeline {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
  }

  .pipeline li {
    display: grid;
    gap: 0.35rem;
    padding: 0.6rem 0.7rem;
    border-left: 3px solid transparent;
    border-radius: 0 var(--radius) var(--radius) 0;
  }

  .pipeline li.focus {
    border-left-color: var(--link);
    background: color-mix(in srgb, var(--link) 7%, transparent);
  }

  .what {
    font-size: var(--t-s);
    font-weight: 600;
  }

  .block {
    display: block;
    font-size: 0.7rem;
    line-height: 1.6;
    padding: 0.5rem 0.6rem;
    background: var(--paper);
    border-radius: 4px;
    word-break: break-all;
    color: var(--ink-soft);
  }

  mark {
    background: color-mix(in srgb, var(--link) 18%, transparent);
    color: var(--ink);
    border-radius: 2px;
  }

  .op {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .op::before {
    content: "";
    display: inline-block;
    width: 1px;
    height: 0.8em;
    background: var(--rule-strong);
    margin-right: 0.5rem;
    vertical-align: -0.1em;
  }

  .result {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
  }

  .vs {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .note {
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .note code {
    color: var(--ink);
    overflow-wrap: anywhere;
  }

  .empty {
    color: var(--ink-soft);
    font-size: var(--t-s);
  }
</style>
