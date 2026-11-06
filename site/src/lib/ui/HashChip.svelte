<script lang="ts">
  import { hashColors } from "../demo/demo.svelte";

  interface Props {
    hash: string;
    /** Text before the hex, for example "genesis". */
    label?: string;
    bad?: boolean;
    title?: string;
  }

  let { hash, label, bad = false, title }: Props = $props();
  const colors = $derived(hashColors(hash));
</script>

<span class="chip" class:bad style:--h={colors[0]} style:--h2={colors[1]} title={title ?? hash}>
  <span class="swatch" aria-hidden="true"></span>
  {#if label}<span class="label">{label}</span>{/if}
  <span class="hex">{hash.slice(0, 6)}</span>
</span>

<style>
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-family: var(--mono);
    font-size: 0.72rem;
    line-height: 1;
    padding: 0.28rem 0.45rem 0.28rem 0.3rem;
    border-radius: 4px;
    background: hsl(var(--h) var(--chip-s) var(--chip-l) / 0.14);
    border: 1px solid hsl(var(--h) var(--chip-s) var(--chip-l) / 0.55);
    white-space: nowrap;
    font-variant-ligatures: none;
  }

  .swatch {
    width: 0.8rem;
    height: 0.8rem;
    border-radius: 2px;
    /* two colors from two bytes of the hash: easy to match by eye, rarely shared by two hashes */
    background: linear-gradient(135deg, hsl(var(--h) var(--chip-s) var(--chip-l)) 50%, hsl(var(--h2) var(--chip-s) var(--chip-l)) 50%);
    flex: none;
  }

  .label {
    font-family: var(--font);
    font-size: 0.72rem;
    color: var(--ink-soft);
  }

  .bad {
    border-color: var(--red);
    background: var(--red-wash);
    color: var(--red);
  }
</style>
