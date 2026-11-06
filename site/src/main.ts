import "@fontsource-variable/schibsted-grotesk";
import "@fontsource-variable/martian-mono";
import "./app.css";

import { mount } from "svelte";

import App from "./App.svelte";

mount(App, { target: document.getElementById("app")! });
