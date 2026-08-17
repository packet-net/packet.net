// ============================================================
// The Users screen's ADMIN list mutations, screens/users.tsx (review item C031, packet.net#692).
//
// users.self.test.tsx (#702 C044) covers the self-service card every scope gets. This covers the
// other half: the admin-only operator list, and the create/delete calls nothing spied before -
// `api.userCreate` and `api.userDelete` had zero references across the whole suite. Creating an
// operator with the wrong scope, or a delete that leaves a ghost row on screen, is an access
// -control mistake an operator would only discover from the node's own behaviour.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, type Scope } from "@/app/auth";
import { Users } from "@/screens/users";
import { api } from "@/lib/api";
import type { UserSummary } from "@/lib/types";

const TOM: UserSummary = {
  username: "tom", scope: "admin", createdUtc: "2026-01-01T00:00:00Z",
  lastLoginUtc: "2026-08-17T09:00:00Z", hasTotp: true, callsign: "M0LTE",
};
const BOB: UserSummary = {
  username: "bob", scope: "read", createdUtc: "2026-08-01T00:00:00Z",
  lastLoginUtc: null, hasTotp: false, callsign: null,
};

/** Mount the screen at a scope, and hand back the /users spy so a test can count refetches. */
function mountUsers(scope: Scope, users: UserSummary[] = [TOM, BOB]) {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
  const list = vi.spyOn(api, "usersList").mockResolvedValue(users);
  render(
    <MemoryRouter>
      <AuthProvider><Users /></AuthProvider>
    </MemoryRouter>,
  );
  return { list };
}

/** The card for one operator in the admin list (they are keyed by username). */
function rowFor(username: string): HTMLElement {
  const name = screen.getAllByText(username).find((el) => el.tagName === "SPAN");
  expect(name, `no list row for ${username}`).toBeDefined();
  const card = name!.closest("div.rounded-xl, div[class*='rounded']");
  expect(card).not.toBeNull();
  return card as HTMLElement;
}

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Users - creating an operator", () => {
  it("posts the username, password and the CHOSEN scope, then refreshes the list", async () => {
    const create = vi.spyOn(api, "userCreate").mockImplementation(async (username, _pw, scope) => ({
      username, scope, createdUtc: "2026-08-17T12:00:00Z", lastLoginUtc: null,
      hasTotp: false, callsign: null,
    }));
    const { list } = mountUsers("admin");
    await waitFor(() => expect(screen.getByText("bob")).toBeInTheDocument());
    const listedOnMount = list.mock.calls.length;

    fireEvent.click(screen.getByRole("button", { name: /Add user/i }));
    const dialog = await screen.findByText("Add operator");
    const modal = dialog.closest("div[class*='rounded']") as HTMLElement;

    fireEvent.change(within(modal).getAllByRole("textbox")[0], { target: { value: " kev " } });
    fireEvent.change(within(modal).getByPlaceholderText("••••••••"), { target: { value: "hunter2hunter2" } });
    fireEvent.change(within(modal).getByRole("combobox"), { target: { value: "operate" } });
    fireEvent.click(within(modal).getByRole("button", { name: /^Create$/i }));

    // The name is trimmed (a stray space would make a second, unreachable account), and the
    // scope is the one the admin picked - not the "read" the form opened with.
    await waitFor(() => expect(create).toHaveBeenCalledWith("kev", "hunter2hunter2", "operate"));
    // The list is refetched, so the new operator appears without a page reload.
    await waitFor(() => expect(list.mock.calls.length).toBeGreaterThan(listedOnMount));
    expect(screen.queryByText("Add operator")).toBeNull();   // and the dialog closed
  });

  it("keeps Create disabled until the name is set and the password is long enough", async () => {
    mountUsers("admin");
    fireEvent.click(screen.getByRole("button", { name: /Add user/i }));
    const modal = (await screen.findByText("Add operator")).closest("div[class*='rounded']") as HTMLElement;
    const create = within(modal).getByRole("button", { name: /^Create$/i });

    expect(create).toBeDisabled();
    fireEvent.change(within(modal).getAllByRole("textbox")[0], { target: { value: "kev" } });
    expect(create).toBeDisabled();
    fireEvent.change(within(modal).getByPlaceholderText("••••••••"), { target: { value: "short" } });
    expect(create).toBeDisabled();
    fireEvent.change(within(modal).getByPlaceholderText("••••••••"), { target: { value: "hunter2hunter2" } });
    expect(create).not.toBeDisabled();
  });

  it("surfaces the server's rejection inside the dialog rather than closing on a failure", async () => {
    vi.spyOn(api, "userCreate").mockRejectedValue(new Error("username 'kev' already exists"));
    mountUsers("admin");

    fireEvent.click(screen.getByRole("button", { name: /Add user/i }));
    const modal = (await screen.findByText("Add operator")).closest("div[class*='rounded']") as HTMLElement;
    fireEvent.change(within(modal).getAllByRole("textbox")[0], { target: { value: "kev" } });
    fireEvent.change(within(modal).getByPlaceholderText("••••••••"), { target: { value: "hunter2hunter2" } });
    fireEvent.click(within(modal).getByRole("button", { name: /^Create$/i }));

    await waitFor(() => expect(screen.getByText(/already exists/)).toBeInTheDocument());
    // Still open, still holding what was typed, so the admin can fix the name and retry.
    expect(screen.getByText("Add operator")).toBeInTheDocument();
    expect(within(modal).getByRole("button", { name: /^Create$/i })).not.toBeDisabled();
  });
});

describe("Users - removing an operator", () => {
  it("deletes by username and refetches the list", async () => {
    const del = vi.spyOn(api, "userDelete").mockResolvedValue(undefined);
    const { list } = mountUsers("admin");
    await waitFor(() => expect(screen.getByText("bob")).toBeInTheDocument());
    const listedOnMount = list.mock.calls.length;

    fireEvent.click(within(rowFor("bob")).getByRole("button", { name: /Remove/i }));

    await waitFor(() => expect(del).toHaveBeenCalledWith("bob"));
    // The row goes because the server said so (a refetch), never optimistically: a delete that
    // 409s must leave the operator on screen.
    await waitFor(() => expect(list.mock.calls.length).toBeGreaterThan(listedOnMount));
  });

  it("leaves the row in place and banners the reason when the delete fails", async () => {
    vi.spyOn(api, "userDelete").mockRejectedValue(new Error("cannot delete the last admin"));
    mountUsers("admin");
    await waitFor(() => expect(screen.getByText("bob")).toBeInTheDocument());

    fireEvent.click(within(rowFor("bob")).getByRole("button", { name: /Remove/i }));

    await waitFor(() => expect(screen.getByText(/cannot delete the last admin/)).toBeInTheDocument());
    expect(screen.getByText("bob")).toBeInTheDocument();
  });
});

describe("Users - the admin gate", () => {
  it("offers no create or delete affordance to a non-admin, and says why", async () => {
    // The server is the real gate (/users is Admin-only); this is the UI mirror, and it must
    // DISABLE rather than hide, so an operator can see what admin would let them do.
    mountUsers("operate", [TOM]);

    const add = screen.getByRole("button", { name: /Add user/i });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute("title", expect.stringMatching(/admin/i));
    expect(screen.getByText(/User management requires the/i)).toBeInTheDocument();
    // No Remove buttons anywhere in the list.
    expect(screen.queryAllByRole("button", { name: /^Remove$/i })).toHaveLength(0);
  });
});
