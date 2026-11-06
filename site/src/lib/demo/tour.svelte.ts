// Shared state of the "How it works" page: the demo ledger, the version and the current step.
// The explainer above the demo uses it to replay an attack in the demo.

import { CURRENT, type Version, VERSIONS } from "../versions";
import { Demo } from "./demo.svelte";

class Tour {
  readonly demo = new Demo();
  version = $state<Version>(CURRENT);
  stepIndex = $state(0);

  constructor() {
    this.goto(0);
  }

  get step() {
    return this.version.steps[this.stepIndex]!;
  }

  goto(index: number): void {
    this.stepIndex = Math.max(0, Math.min(index, this.version.steps.length - 1));
    this.step.enter?.(this.demo);
  }

  gotoId(id: string): void {
    const index = this.version.steps.findIndex((s) => s.id === id);
    if (index >= 0) this.goto(index);
  }

  chooseVersion(id: string): void {
    const v = VERSIONS.find((x) => x.id === id && x.released);
    if (v) {
      this.version = v;
      this.goto(0);
    }
  }
}

export const tour = new Tour();
