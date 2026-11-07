<script lang="ts" module>
  import type { Language } from "../versions";

  const KEY = "provance-demo-language";

  function load(): Language {
    try {
      const v = localStorage.getItem(KEY);
      if (v === "dotnet" || v === "java" || v === "python") return v;
    } catch {
      /* storage unavailable */
    }
    return "dotnet";
  }

  let language = $state<Language>(load());

  function choose(l: Language) {
    language = l;
    try {
      localStorage.setItem(KEY, l);
    } catch {
      /* storage unavailable */
    }
  }
</script>

<script lang="ts">
  import { LANGUAGES } from "../versions";

  let { samples, install = false }: { samples: Record<Language, string>; install?: boolean } = $props();
  const id = $props.id();
  const current = $derived(LANGUAGES.find((l) => l.id === language)!);
</script>

<div class="code">
  <div class="tabs" role="tablist" aria-label="Language">
    {#each LANGUAGES as l (l.id)}
      <button
        role="tab"
        id="{id}-{l.id}"
        aria-selected={language === l.id}
        aria-controls="{id}-panel"
        onclick={() => choose(l.id)}>{l.label}</button>
    {/each}
  </div>
  <div id="{id}-panel" role="tabpanel" aria-labelledby="{id}-{language}">
    {#if install}<pre class="install"><code>{current.install}</code></pre>{/if}
    <pre><code>{samples[language]}</code></pre>
  </div>
</div>

<style>
  .code {
    border: 1px solid var(--rule-strong);
    border-radius: var(--radius);
    overflow: hidden;
    background: var(--sheet);
  }

  .tabs {
    display: flex;
    border-bottom: 1px solid var(--rule);
  }

  .tabs button {
    border: none;
    background: none;
    padding: 0.45rem 0.8rem;
    font-size: var(--t-xs);
    font-weight: 600;
    color: var(--ink-soft);
    cursor: pointer;
    border-bottom: 2px solid transparent;
    margin-bottom: -1px;
  }

  .tabs button[aria-selected="true"] {
    color: var(--ink);
    border-bottom-color: var(--ink);
  }

  /* long lines wrap instead of adding a scroll bar; the indentation is kept */
  pre {
    margin: 0;
    padding: 0.75rem 0.85rem;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 0.72rem;
    line-height: 1.65;
  }

  pre code {
    font-size: inherit;
  }

  .install {
    border-bottom: 1px dashed var(--rule);
    color: var(--ink-soft);
  }
</style>
