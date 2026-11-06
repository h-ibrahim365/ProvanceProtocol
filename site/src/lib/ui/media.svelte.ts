// Viewport width as reactive state, for the few places where the phone layout behaves differently.

class Media {
  /** Two-column layout (side panel next to the table). */
  wide = $state(true);

  constructor() {
    if (typeof matchMedia !== "function") return;
    const query = matchMedia("(min-width: 1000px)");
    this.wide = query.matches;
    query.addEventListener("change", (e) => (this.wide = e.matches));
  }
}

export const media = new Media();

/** On phones, brings an element into view after a tap changed it, unless it is already visible. */
export function reveal(id: string, block: ScrollLogicalPosition = "start"): void {
  if (media.wide) return;
  const el = document.getElementById(id);
  if (!el) return;
  const r = el.getBoundingClientRect();
  if (r.top < 0 || r.top > innerHeight * 0.6) {
    const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
    el.scrollIntoView({ behavior: reduced ? "auto" : "smooth", block });
  }
}
