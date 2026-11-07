<script lang="ts">
  import Demo from "./lib/demo/Demo.svelte";
  import Explainer from "./lib/demo/Explainer.svelte";
  import Releases from "./lib/releases/Releases.svelte";
  import { untrack } from "svelte";
  import { tour } from "./lib/demo/tour.svelte";

  type Page = "demo" | "releases";

  function pageFromHash(): Page {
    return location.hash === "#releases" ? "releases" : "demo";
  }

  let page = $state<Page>(pageFromHash());

  /** Links such as #try-retention open that step of the demo (used by the documentation). */
  function openLinkedStep() {
    const id = location.hash.startsWith("#try-") ? location.hash.slice(5) : null;
    if (id && untrack(() => tour.openStep(id))) {
      requestAnimationFrame(() => document.getElementById("try")?.scrollIntoView());
    }
  }

  $effect(() => {
    const update = () => {
      page = pageFromHash();
      openLinkedStep();
    };
    openLinkedStep();
    addEventListener("hashchange", update);
    return () => removeEventListener("hashchange", update);
  });
</script>

<header class="top">
  <a class="mark" href="#demo" aria-label="PROVANCE demo, home">
    <img src="/logo.png" alt="" width="25" height="34" />
    <span>PROVANCE</span>
  </a>
  <nav aria-label="Pages">
    <a href="#demo" aria-current={page === "demo" ? "page" : undefined}>How it works</a>
    <a href="#releases" aria-current={page === "releases" ? "page" : undefined}>Releases</a>
    <a class="ext" href="https://github.com/h-ibrahim365/ProvanceProtocol">GitHub</a>
  </nav>
</header>

<main>
  {#if page === "demo"}
    <section class="hero">
      <h1>An audit log that shows when someone changed it.</h1>
      <p>
        PROVANCE chains and signs every entry your application records, in your own database. Below: how it holds
        up against someone who can edit that database, then a real ledger running in your browser to try it yourself.
      </p>
      <p>
        <button class="btn" onclick={() => document.getElementById("try")?.scrollIntoView({ behavior: "smooth" })}>Go to the demo</button>
      </p>
    </section>
    <Explainer />
    <section id="try" class="try" aria-labelledby="try-title">
      <h2 id="try-title">Try it yourself</h2>
      <p>Follow the steps, or jump to any of them. Everything happens in your browser.</p>
    </section>
    <Demo />
  {:else}
    <Releases />
  {/if}
</main>

<footer class="bottom">
  <p>
    Open source under the MIT license. The demo computes the same hashes as the .NET, Java and Python libraries,
    checked against the <a href="https://github.com/h-ibrahim365/ProvanceProtocol/tree/main/docs/test-vectors">published test vectors</a>.
  </p>
</footer>

<style>
  .top {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem 1.5rem;
    padding-block: 0.9rem;
    padding-left: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-left, 0px));
    padding-right: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-right, 0px));
    border-bottom: 1px solid var(--rule);
  }

  .mark {
    display: inline-flex;
    align-items: center;
    gap: 0.55rem;
    color: var(--ink);
    text-decoration: none;
    font-weight: 800;
    letter-spacing: 0.06em;
  }

  nav {
    display: flex;
    gap: 0.3rem;
    font-size: var(--t-s);
  }

  nav a {
    color: var(--ink-soft);
    text-decoration: none;
    padding: 0.35rem 0.7rem;
    border-radius: var(--radius);
    font-weight: 600;
  }

  nav a:hover {
    color: var(--ink);
    background: color-mix(in srgb, var(--ink) 6%, transparent);
  }

  nav a[aria-current="page"] {
    color: var(--ink);
    box-shadow: inset 0 -2px 0 var(--ink);
    border-radius: 0;
  }

  main {
    padding-block: clamp(1.2rem, 3vw, 2.5rem) 3rem;
    padding-left: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-left, 0px));
    padding-right: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-right, 0px));
    max-width: 92rem;
    margin: 0 auto;
  }

  .hero {
    display: grid;
    gap: 0.7rem;
    max-width: 50rem;
    margin-bottom: clamp(1.5rem, 3vw, 2.5rem);
  }

  h1 {
    font-size: var(--t-xxl);
    font-weight: 800;
    letter-spacing: -0.025em;
    line-height: 1.05;
    max-width: 22ch;
  }

  .hero p {
    color: var(--ink-soft);
    max-width: 62ch;
  }

  .try {
    display: grid;
    gap: 0.4rem;
    margin: clamp(2.5rem, 5vw, 4rem) 0 1.5rem;
    padding-top: clamp(1.5rem, 3vw, 2.5rem);
    border-top: 3px double var(--red);
    scroll-margin-top: 1rem;
  }

  .try h2 {
    font-size: var(--t-xl);
  }

  .try p {
    color: var(--ink-soft);
  }

  .bottom {
    border-top: 1px solid var(--rule);
    padding-block: 1.2rem 2rem;
    padding-left: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-left, 0px));
    padding-right: max(clamp(1rem, 3vw, 2.5rem), env(safe-area-inset-right, 0px));
    font-size: var(--t-xs);
    color: var(--ink-soft);
  }

  .bottom p {
    max-width: 80ch;
  }

  @media (max-width: 640px) {
    .top {
      padding-block: 0.7rem 0;
      gap: 0.5rem;
    }

    /* the pages become three equal tabs under the logo */
    nav {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      width: 100%;
      gap: 0;
    }

    nav a {
      text-align: center;
      padding: 0.75rem 0.3rem;
      border-radius: 0;
    }

    .hero {
      gap: 0.6rem;
    }

    .hero p {
      font-size: var(--t-m);
    }

    .hero .btn {
      width: 100%;
      justify-content: center;
    }
  }
</style>
