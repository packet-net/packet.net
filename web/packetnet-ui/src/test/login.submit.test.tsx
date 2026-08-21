// ============================================================
// The Login screen's SUBMIT path, screens/login.tsx (review item C031, packet.net#692).
//
// The two tests it had covered only the passkey gate (whether the button renders in a secure
// context). Nothing exercised signing in: not the credentials it sends, not the 401 message,
// not the lockout, not the passkey ceremony, and not `?next=` - which the app-gateway's
// re-auth redirect sets and which is an open-redirect surface if it is honoured naively.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Login } from "@/screens/login";
import { api, Unauthorized } from "@/lib/api";
import type { LoginResult } from "@/lib/types";

const navigate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => navigate,
}));

const TOKENS: LoginResult = {
  token: "jwt", expiresAt: "2026-08-17T13:00:00Z", scopes: "operate",
  refreshToken: "rt-1", username: "tom",
};

function mountLogin() {
  return render(
    <MemoryRouter initialEntries={["/login"]}>
      <AuthProvider><Login /></AuthProvider>
    </MemoryRouter>,
  );
}

function signIn(username = "tom", password = "hunter2") {
  const boxes = screen.getAllByRole("textbox");
  fireEvent.change(boxes[0], { target: { value: username } });
  fireEvent.change(screen.getByPlaceholderText("••••••••"), { target: { value: password } });
  fireEvent.click(screen.getByRole("button", { name: /^Sign in$/i }));
}

/** Make the platform look like a secure context, so the passkey affordance renders. */
function withSecureContext(): () => void {
  const prevSecure = window.isSecureContext;
  const prevPkc = (window as { PublicKeyCredential?: unknown }).PublicKeyCredential;
  Object.defineProperty(window, "isSecureContext", { value: true, configurable: true });
  (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = function () {};
  return () => {
    Object.defineProperty(window, "isSecureContext", { value: prevSecure, configurable: true });
    (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = prevPkc;
  };
}

beforeEach(() => {
  localStorage.clear();
  window.history.replaceState({}, "", "/login");
});

afterEach(() => {
  navigate.mockClear();
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Login - password submit", () => {
  it("signs in with the typed credentials, persists the pair the SERVER named, and enters the app", async () => {
    const login = vi.spyOn(api, "login").mockResolvedValue(TOKENS);
    mountLogin();

    signIn("tom", "hunter2");

    await waitFor(() => expect(login).toHaveBeenCalledWith("tom", "hunter2"));
    await waitFor(() => expect(navigate).toHaveBeenCalledWith("/", { replace: true }));
    // The identity comes off the response, not the typed box (a passkey sign-in leaves the box
    // empty), and the refresh token is persisted alongside so a reload can renew silently.
    const session = JSON.parse(localStorage.getItem("pdn.session") ?? "{}") as Record<string, unknown>;
    expect(session).toMatchObject({ token: "jwt", refreshToken: "rt-1", username: "tom", scope: "operate" });
  });

  it("shows one generic message on a 401 and stays on the form, ready to retry", async () => {
    // The server deliberately never says WHICH of username/password was wrong; the screen must
    // not invent a distinction either.
    vi.spyOn(api, "login").mockRejectedValue(new Unauthorized("Invalid username or password."));
    mountLogin();

    signIn("tom", "wrong");

    await waitFor(() => expect(screen.getByText("Invalid username or password.")).toBeInTheDocument());
    expect(navigate).not.toHaveBeenCalled();
    expect(localStorage.getItem("pdn.session")).toBeNull();
    // Not left spinning: the operator can correct the password and try again.
    expect(screen.getByRole("button", { name: /^Sign in$/i })).not.toBeDisabled();
  });

  it("shows the server's own message on a 429 lockout, not the generic wrong-password one", async () => {
    // A lockout is not a credential error: telling the operator to check their password while
    // the node is refusing every attempt for a minute is actively misleading.
    vi.spyOn(api, "login").mockRejectedValue(new Error("Too many login attempts. Try again in 60 s."));
    mountLogin();

    signIn();

    await waitFor(() => expect(screen.getByText(/Too many login attempts/)).toBeInTheDocument());
    expect(screen.queryByText("Invalid username or password.")).toBeNull();
    expect(navigate).not.toHaveBeenCalled();
  });

  it("keeps Sign in disabled until both fields are filled", () => {
    mountLogin();
    const submit = screen.getByRole("button", { name: /^Sign in$/i });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getAllByRole("textbox")[0], { target: { value: "tom" } });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByPlaceholderText("••••••••"), { target: { value: "hunter2" } });
    expect(submit).not.toBeDisabled();
  });

  it("honours a same-site ?next= but never an off-site one", async () => {
    // The app-gateway's re-auth redirect sets ?next=/apps/bbs so the operator lands back where
    // they were. Anything that could leave this origin is dropped: a protocol-relative //host,
    // any scheme, and a backslash (which some parsers fold to a slash).
    const login = vi.spyOn(api, "login").mockResolvedValue(TOKENS);

    for (const [next, expected] of [
      ["/apps/bbs", "/apps/bbs"],
      ["//evil.example/x", "/"],
      ["https://evil.example", "/"],
      ["/\\evil.example", "/"],
    ] as const) {
      navigate.mockClear();
      login.mockClear();
      localStorage.clear();
      window.history.replaceState({}, "", `/login?next=${encodeURIComponent(next)}`);
      const view = mountLogin();
      signIn();
      await waitFor(() => expect(navigate).toHaveBeenCalledWith(expected, { replace: true }));
      view.unmount();
    }
  });
});

describe("Login - the passkey path", () => {
  it("runs the ceremony and enters with the SERVER-resolved identity, not the typed box", async () => {
    const restore = withSecureContext();
    try {
      const assert = vi.spyOn(api, "passkeyAssert")
        .mockResolvedValue({ ...TOKENS, username: "tom", scopes: "admin" });
      mountLogin();

      // A discoverable credential: the operator types nothing, so the allow-list is unscoped.
      fireEvent.click(screen.getByRole("button", { name: /Continue with passkey/i }));

      await waitFor(() => expect(assert).toHaveBeenCalledWith(undefined));
      await waitFor(() => expect(navigate).toHaveBeenCalledWith("/", { replace: true }));
      const session = JSON.parse(localStorage.getItem("pdn.session") ?? "{}") as Record<string, unknown>;
      expect(session).toMatchObject({ username: "tom", scope: "admin" });
    } finally {
      restore();
    }
  });

  it("scopes the allow-list to the typed username when there is one", async () => {
    const restore = withSecureContext();
    try {
      const assert = vi.spyOn(api, "passkeyAssert").mockResolvedValue(TOKENS);
      mountLogin();

      fireEvent.change(screen.getAllByRole("textbox")[0], { target: { value: " tom " } });
      fireEvent.click(screen.getByRole("button", { name: /Continue with passkey/i }));

      await waitFor(() => expect(assert).toHaveBeenCalledWith("tom"));
    } finally {
      restore();
    }
  });

  it("says nothing when the operator cancels the ceremony, but surfaces a real failure", async () => {
    const restore = withSecureContext();
    try {
      const assert = vi.spyOn(api, "passkeyAssert")
        .mockRejectedValueOnce(new DOMException("cancelled", "NotAllowedError"))
        .mockRejectedValueOnce(new Unauthorized("nope"));
      mountLogin();
      const button = screen.getByRole("button", { name: /Continue with passkey/i });

      // Dismissing the platform's own passkey sheet is not an error to shout about.
      fireEvent.click(button);
      await waitFor(() => expect(assert).toHaveBeenCalledTimes(1));
      await waitFor(() => expect(screen.getByRole("button", { name: /Continue with passkey/i })).not.toBeDisabled());
      expect(screen.queryByText(/not recognised/i)).toBeNull();

      // A credential the node rejects IS worth saying out loud.
      fireEvent.click(screen.getByRole("button", { name: /Continue with passkey/i }));
      await waitFor(() => expect(screen.getByText(/That passkey was not recognised/i)).toBeInTheDocument());
      expect(navigate).not.toHaveBeenCalled();
    } finally {
      restore();
    }
  });

  it("hides the passkey affordance entirely on a plain-HTTP LAN node", () => {
    // jsdom's default: not a secure context. A button that can only fail is worse than none,
    // so the affordance is absent - and so is any hint about it. The page does not tell the
    // operator to reach the node some other way; password login just carries the flow.
    mountLogin();
    expect(screen.queryByRole("button", { name: /Continue with passkey/i })).toBeNull();
    expect(screen.queryByText(/passkey/i)).toBeNull();
    expect(screen.queryByText(/HTTPS/i)).toBeNull();
    expect(screen.queryByText(/Tailscale/i)).toBeNull();
    expect(screen.getByRole("button", { name: /^Sign in$/i })).toBeInTheDocument();
  });
});
