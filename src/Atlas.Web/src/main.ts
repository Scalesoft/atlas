import { createApp } from "vue";
import { lazyComponent } from "./lazyComponent";
import "@fontsource-variable/dm-sans";
import App from "./App.vue";
import "./styles/tailwind.css";
import "./shell.css";
import "./components/library/segmentedThumb.css";
import "./styles/palettes.css";
import "./styles/appearance.css";
import "@atlas/markdown/styles/states.css";
// The menu look of ActionMenu and ActionDisclosure: settings and the profile show menus before any editor loads its stylesheet.
import "@atlas/markdown/styles/menu.css";
import "./motion.css";
import "./styles/ai.css";
import "./styles/refresh.css";
import "./styles/refresh-library.css";
import "./styles/refresh-article.css";
import "./styles/refresh-theme.css";
import { closeReauthReturn } from "./reauthPopup";
import { textsLoaded } from "./i18n";
const root = import.meta.env.DEV && new URLSearchParams(location.search).get("gallery") === "1"
  ? lazyComponent(() => import("./components/dev/ComponentGallery.vue"))
  : App;
const mount = () => createApp(root).mount("#app");
// The Hub popup of a fresh sign-in lands here after its callback and only closes itself. Another language than Czech mounts
// once the texts of the first view are in (src/i18n): nothing renders twice.
if (!closeReauthReturn()) textsLoaded()?.then(mount) ?? mount();
