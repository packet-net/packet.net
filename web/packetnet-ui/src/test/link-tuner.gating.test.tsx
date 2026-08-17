// Link tuner scope gating (#702 C047).
//
// PdnPortTuningApi maps session / next / stop / txdelay-min under
// RequireAuthorization(Admin) - only the /tuning/events feed is Read. The screen imported no
// auth helper at all, so Start/Next/Stop rendered live for any authenticated operator and the
// click came back 403 with nothing to explain it. The convention is the one headends.tsx uses
// for keyup: disable + say why in the title.
import { describe, it, expect, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, type Scope } from "@/app/auth";
import { LinkTuner } from "@/screens/link-tuner";

const ADMIN_HINT = "Deviation tuning requires the admin scope";

async function mountTuner(scope: Scope) {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
  const result = render(
    <MemoryRouter initialEntries={["/tools/tuner?port=vhf-1"]}>
      <AuthProvider><LinkTuner /></AuthProvider>
    </MemoryRouter>,
  );
  await waitFor(() => expect(screen.getByText(/Deviation tuning/i)).toBeInTheDocument());
  // A valid 8-char peer id, so the only thing left gating Start is the scope.
  fireEvent.change(screen.getByPlaceholderText(/8 chars/i), { target: { value: "12345678" } });
  return result;
}

afterEach(() => localStorage.clear());

describe("LinkTuner — admin gating", () => {
  it("refuses to start a session at operate scope and says why", async () => {
    await mountTuner("operate");

    const start = screen.getByRole("button", { name: /Start tuning/i });
    await waitFor(() => expect(start).toBeDisabled());
    expect(start).toHaveAttribute("title", ADMIN_HINT);
    // The setup copy tells the operator what they can still do (watch a session).
    expect(screen.getByText(/admin-scoped/)).toBeInTheDocument();
  });

  it("enables Start for an admin", async () => {
    await mountTuner("admin");

    await waitFor(() =>
      expect(screen.getByRole("button", { name: /Start tuning/i })).toBeEnabled());
    expect(screen.queryByText(/admin-scoped/)).toBeNull();
  });

  it("gates Next round and Stop on the same scope once a session is live", async () => {
    // Start the session as admin (the mock backend arms it), then assert the in-session
    // controls carry the gate too - they hit the same Admin-only endpoints.
    await mountTuner("admin");
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /Start tuning/i })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: /Start tuning/i }));

    await waitFor(() => expect(screen.getByText(/paused for tuning/i)).toBeInTheDocument());
    // Stop is always actionable for an admin; Next waits for the round to ask for a pot turn.
    expect(screen.getByRole("button", { name: /Stop/i })).toBeEnabled();
    await waitFor(
      () => expect(screen.getByRole("button", { name: /Next round/i })).toBeEnabled(),
      { timeout: 3000 },
    );
  });
});
