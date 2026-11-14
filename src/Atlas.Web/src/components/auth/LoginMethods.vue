<script setup lang="ts">
import {
  nextTick,
  onMounted,
  onUnmounted,
  reactive,
  ref,
} from "vue";
import { lazyComponent } from "../../lazyComponent";
const AdminRecovery = lazyComponent(() => import("./AdminRecovery.vue"));
import { api, clearCsrf, errorMessage, post, type User } from "../../api";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import FormField from "../FormField.vue";
import AtlasVersionInfo from "../AtlasVersionInfo.vue";
import AtlasIcon from "../AtlasIcon.vue";
import type { AuthMethods } from "../../administration";
import { animateAtlas, motionMs } from "../../motion/aiMotion";
import { disclosureHooks } from "../settings/disclosure";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import t from "../../i18n/components-login";
defineProps<{ version?: string }>();
const emit = defineEmits<{ signedIn: [user: User] }>();
const methods = ref<AuthMethods>(),
  loading = ref(true),
  busy = ref(false),
  /** The busy label belongs to the button that was pressed (SH-8). */
  developerBusy = ref(false),
  error = ref(""),
  notice = ref(""),
  activating = ref(false),
  recovering = ref(false);
const credentials = reactive({ username: "", password: "" }),
  activation = reactive({ token: "", newPassword: "", confirmPassword: "" });
let disposed = false;
// Sign-in and activation swap like the steps of a settings flow (leave fast, rise in), and the stage between them eases
// from the height of the one that left to the one that arrives, so the card and everything under the form glide
// instead of jumping by the difference. The height is measured to whatever follows the stage: the margin that collapses
// out of the last field belongs to the distance, or the link under the form would move 9 px when the animation ends.
// The administrator recovery replaces the whole login the same way, so it has a stage of its own around both.
const stage = ref<HTMLElement>();
const shell = ref<HTMLElement>();
function stageMotion(target: typeof stage) {
  let leaving = 0;
  function reach() {
    const node = target.value;
    if (!node) return 0;
    const box = node.getBoundingClientRect();
    const next = node.nextElementSibling;
    return next ? next.getBoundingClientRect().top - box.top : box.height;
  }
  return {
    remember() { leaving = reach(); },
    ease() {
      const node = target.value;
      const from = leaving;
      leaving = 0;
      if (!node || !from) return;
      // What arrives may still grow (the recovery asks the server what it offers): when the glide ends and the natural
      // height is somewhere else, it goes on from where it stopped instead of jumping the rest in one frame.
      const glide = (start: number, end: number, rounds: number) => {
        if (Math.abs(end - start) < 2) return;
        const animation = animateAtlas(node, [{ height: `${start}px`, overflow: "clip" }, { height: `${end}px`, overflow: "clip" }], { duration: motionMs(node, "--atlas-motion-base"), fill: "none" });
        animation?.addEventListener("finish", () => { if (rounds > 0) glide(end, reach(), rounds - 1); });
      };
      glide(from, reach(), 2);
    },
  };
}
const formStage = stageMotion(stage), loginStage = stageMotion(shell);
async function load() {
  loading.value = true;
  error.value = "";
  try {
    const result = await api<AuthMethods>("/auth/methods");
    if (!disposed) methods.value = result;
  } catch (e) {
    if (!disposed) error.value = errorMessage(e);
  } finally {
    loading.value = false;
  }
  // Where the keyboard starts (keyboard pass): the first way to sign in, a provider or the user name, unless the reader is elsewhere.
  await nextTick();
  if (!disposed && (!document.activeElement || document.activeElement === document.body))
    shell.value?.querySelector<HTMLElement>('.login-provider, input[autocomplete="username"]')?.focus();
}
async function login() {
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    const user = await post<User>("/auth/login", { ...credentials });
    credentials.password = "";
    clearCsrf();
    emit("signedIn", user);
  } catch (e) {
    error.value = errorMessage(e);
  } finally {
    busy.value = false;
  }
}
// The server offers this only to a local Development build (see /api/auth/methods); it signs in as the oldest wiki administrator.
async function developerLogin() {
  if (busy.value) return;
  busy.value = developerBusy.value = true;
  error.value = "";
  try {
    const user = await post<User>("/auth/developer");
    clearCsrf();
    emit("signedIn", user);
  } catch (e) {
    error.value = errorMessage(e);
  } finally {
    busy.value = developerBusy.value = false;
  }
}
async function activate() {
  if (busy.value) return;
  error.value = "";
  if (activation.newPassword !== activation.confirmPassword) {
    error.value = t.passwordMismatch;
    return;
  }
  busy.value = true;
  try {
    await post("/auth/activate", {
      token: activation.token.trim(),
      newPassword: activation.newPassword,
    });
    Object.assign(activation, {
      token: "",
      newPassword: "",
      confirmPassword: "",
    });
    activating.value = false;
    notice.value = t.activated;
    clearCsrf();
  } catch (e) {
    error.value = errorMessage(e);
  } finally {
    busy.value = false;
  }
}
function toggleActivation() {
  if (busy.value) return;
  activating.value = !activating.value;
  error.value = "";
  notice.value = "";
  credentials.password = "";
  Object.assign(activation, {
    token: "",
    newPassword: "",
    confirmPassword: "",
  });
}
function beginRecovery() {
  recovering.value = true;
  activating.value = false;
  credentials.password = "";
  Object.assign(activation, {
    token: "",
    newPassword: "",
    confirmPassword: "",
  });
  error.value = "";
  notice.value = "";
}
function recovered(user: User) {
  recovering.value = false;
  emit("signedIn", user);
}
function externalLogin(loginUrl: string) {
  if (busy.value) return;
  const url = new URL(loginUrl, location.origin);
  if (
    url.origin !== location.origin ||
    !url.pathname.startsWith("/api/auth/oidc/")
  ) {
    error.value = t.badProviderUrl;
    return;
  }
  url.searchParams.set("returnUrl", location.pathname + location.hash);
  busy.value = true;
  location.assign(url.pathname + url.search);
}
onMounted(async () => {
  await load();
  const url = new URL(location.href);
  if (url.searchParams.has("authError")) {
    error.value = t.externalFailed;
    url.searchParams.delete("authError");
    history.replaceState(null, "", url.pathname + url.search + url.hash);
  }
});
onUnmounted(() => {
  disposed = true;
  credentials.password = "";
  Object.assign(activation, {
    token: "",
    newPassword: "",
    confirmPassword: "",
  });
});
</script>
<template>
  <section class="login-methods" :aria-label="t.label">
    <LoadingSkeleton v-if="loading" :rows="3" row-height="64px" :label="t.loading" />
    <div v-else ref="shell" class="login-shell"
    ><Transition name="login-step" mode="out-in" @before-leave="loginStage.remember" @enter="loginStage.ease"
    ><AdminRecovery
      v-if="recovering"
      key="recovery"
      @cancel="recovering = false"
      @recovered="recovered"
    />
    <div v-else-if="methods" key="main"
      ><div ref="stage" class="login-stage"
      ><Transition name="login-step" mode="out-in" @before-leave="formStage.remember" @enter="formStage.ease"
      ><div v-if="!activating" key="sign-in"
        ><div v-if="methods.providers.length" class="external-methods">
          <Button v-for="provider in methods.providers" :key="provider.id"
                  variant="outline" class="login-provider" type="button"
                  :disabled="busy" @click="externalLogin(provider.loginUrl)">
            <AtlasIcon name="shield-check" :size="18" aria-hidden="true" />
            <span>{{ t.continueWith(provider.displayName) }}</span>
            <AtlasIcon name="external" :size="15" aria-hidden="true" />
          </Button>
          <p v-if="methods.builtinEnabled" class="login-separator">
            {{ t.orLocal }}
          </p>
        </div>
        <form v-if="methods.builtinEnabled" @submit.prevent="login">
          <fieldset :disabled="busy">
            <FormField :label="t.username"
              ><Input
                v-model="credentials.username"
                required
                autocomplete="username" /></FormField
            ><FormField :label="t.password"
              ><Input
                v-model="credentials.password"
                required
                type="password"
                autocomplete="current-password"
            /></FormField>
          </fieldset>
          <Button variant="default" :disabled="busy">
            {{ busy && !developerBusy ? t.signingIn : t.signIn }}
          </Button>
        </form>
        <p
          v-if="!methods.builtinEnabled && !methods.providers.length"
          class="notice error"
        >
          {{ t.noMethod }}
        </p>
        <div v-if="methods.developerSignIn" class="developer-sign-in">
          <Button variant="secondary" type="button" :disabled="busy" @click="developerLogin">
            {{ developerBusy ? t.signingIn : t.developer }}
          </Button>
          <p class="help">{{ t.developerHelp }}</p>
        </div></div
      >
      <form
        v-else
        key="activate"
        :aria-label="t.activationLabel"
        @submit.prevent="activate"
      >
        <h2>{{ t.activationTitle }}</h2>
        <p class="help">
          {{ t.activationHelp }}
        </p>
        <fieldset :disabled="busy">
          <FormField :label="t.activationCode"
            ><Input
              v-model="activation.token"
              required
              type="password"
              autocomplete="off"
              spellcheck="false" /></FormField
          ><FormField :label="t.newPassword"
            ><Input
              v-model="activation.newPassword"
              required
              type="password"
              minlength="12"
              maxlength="128"
              autocomplete="new-password" /></FormField
          ><FormField :label="t.confirmPassword"
            ><Input
              v-model="activation.confirmPassword"
              required
              type="password"
              minlength="12"
              maxlength="128"
              autocomplete="new-password"
          /></FormField>
          <p class="help">{{ t.passwordRule }}</p>
        </fieldset>
        <Button variant="default" :disabled="busy">
          {{ busy ? t.settingPassword : t.setPassword }}
        </Button>
      </form></Transition></div>
      <button
        v-if="methods.builtinEnabled"
        class="login-alternative"
        type="button"
        :disabled="busy"
        @click="toggleActivation"
      >
        {{ activating ? t.backToSignIn : t.activate }}</button
      >
      <!-- Administrator recovery is a rare emergency path; it waits in the version details. -->
      <AtlasVersionInfo class="entry-version" :version="version"
        ><button
          v-if="!activating"
          class="login-alternative login-recovery"
          type="button"
          :disabled="busy"
          @click="beginRecovery"
        >
          {{ t.recover }}
        </button></AtlasVersionInfo
      ></div
    ></Transition></div>
    <!-- A message unfolds under the form instead of popping in and moving it. -->
    <Transition :css="false" v-bind="disclosureHooks"><p v-if="notice" class="notice success" role="status">{{ notice }}</p></Transition>
    <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="notice error" role="alert">{{ error }}</p></Transition>
    <Button variant="outline"
 v-if="!methods && !loading"

 type="button"
 @click="load">
      {{ t.reload }}
    </Button>
  </section>
</template>

<style scoped>
.login-methods { min-width: 0; }
.external-methods { display: grid; gap: 10px; }
.login-provider { width: 100%; height: auto; min-height: 48px; justify-content: flex-start; gap: 12px; padding: 12px 14px; text-align: left; white-space: normal; }
.login-provider span { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.login-provider svg { flex: none; }
.login-separator { display: flex; align-items: center; gap: 12px; color: var(--muted); font-size: 12px; margin: 12px 0 0; }
.login-separator::before, .login-separator::after { content: ""; flex: 1; height: 1px; background: var(--line); }
.developer-sign-in { margin-top: 12px; }
.developer-sign-in > [data-slot="button"] { width: 100%; }
.developer-sign-in .help { margin: 6px 0 0; text-align: center; }
/* The same rise and fade as the steps of a settings flow (.st-step-*): reduced motion keeps only the fade. */
.login-step-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.login-step-leave-active { animation: st-rise-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
</style>
