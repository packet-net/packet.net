// The Users screen's self-service surface (#702 C044).
//
// /users is Admin-gated as a whole, while the WebAuthn register/credentials and TOTP enroll
// endpoints are Read-gated ("any authenticated user may add a passkey"). The panel used to
// render the passkey/TOTP rows ONLY inside the per-user cards built from the admin-only list,
// so a read or operate operator got an empty list (its error swallowed) and no enrolment path
// at all. The card is now rendered from the SESSION, for every scope.
//
// passkeysAvailable() reads window.isSecureContext + window.PublicKeyCredential (jsdom has
// neither by default) and api.totpSupported() is false in mock mode, so both are simulated
// here — otherwise the controls render in their "needs HTTPS" / "needs a live node" shape and
// this would assert nothing about the enrolment path.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, type Scope } from "@/app/auth";
import { Users } from "@/screens/users";
import { api } from "@/lib/api";

function seedScope(scope: Scope) {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

function mountUsers(scope: Scope) {
  seedScope(scope);
  return render(
    <MemoryRouter>
      <AuthProvider><Users /></AuthProvider>
    </MemoryRouter>,
  );
}

function selfCard(): HTMLElement {
  const el = document.querySelector('[data-testid="self-account"]');
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

let prevSecure: boolean;
let prevPkc: unknown;

beforeEach(() => {
  prevSecure = window.isSecureContext;
  prevPkc = (window as { PublicKeyCredential?: unknown }).PublicKeyCredential;
  Object.defineProperty(window, "isSecureContext", { value: true, configurable: true });
  (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = function () {};
  vi.spyOn(api, "totpSupported").mockReturnValue(true);
});

afterEach(() => {
  Object.defineProperty(window, "isSecureContext", { value: prevSecure, configurable: true });
  (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = prevPkc;
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Users — your own account", () => {
  it("offers passkey + over-RF enrolment at read scope, with no admin list", async () => {
    mountUsers("read");

    const card = await waitFor(() => selfCard());
    expect(within(card).getByText("Your account")).toBeInTheDocument();
    expect(within(card).getByText("tom")).toBeInTheDocument();
    // The two self-service affordances the Read-gated endpoints back.
    expect(within(card).getByRole("button", { name: /Add passkey/i })).toBeEnabled();
    await waitFor(() =>
      expect(within(card).getByRole("button", { name: /Enrol authenticator/i })).toBeEnabled());
    // And the screen still says user MANAGEMENT is admin's.
    expect(screen.getByText(/User management requires the/)).toBeInTheDocument();
  });

  it("shows the same card at admin scope, and the list rows stay self-service indicators", async () => {
    mountUsers("admin");

    const card = await waitFor(() => selfCard());
    expect(within(card).getByRole("button", { name: /Add passkey/i })).toBeInTheDocument();
    // Wait for the operator list itself (the mock has one row, "tom" — the signed-in user).
    await waitFor(() => expect(screen.getAllByText(/last login/).length).toBeGreaterThan(0));
    // Exactly one enrolment surface in the whole screen: the rows below (including the
    // signed-in user's own) carry the static indicator, not a second set of controls.
    expect(screen.getAllByRole("button", { name: /Add passkey/i })).toHaveLength(1);
    expect(screen.getAllByText("self-service").length).toBeGreaterThan(0);
  });

  it("surfaces a failed operator-list load instead of an empty list", async () => {
    vi.spyOn(api, "usersList").mockRejectedValue(new Error("/users: 403 Forbidden"));
    mountUsers("operate");

    await waitFor(() =>
      expect(screen.getByText(/Couldn't load the operator list/)).toBeInTheDocument());
    expect(screen.getByText(/403 Forbidden/)).toBeInTheDocument();
    // The enrolment path is unaffected by the list failing.
    expect(within(selfCard()).getByRole("button", { name: /Add passkey/i })).toBeInTheDocument();
  });
});
