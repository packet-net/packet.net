// The NET/ROM + INP3 config form against the server's JSON wire dialect: `routing` is
// an enum MEMBER NAME ("Transit"), not an integer, and the four INP3 timing knobs are
// numbers of SECONDS, not "hh:mm:ss" duration strings. Both used to be wrong on the
// wire (the server emitted ints + TimeSpan strings), which left the routing picker
// showing the wrong option and 400'd any save that touched an INP3 field.
import { describe, it, expect } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Config } from "@/screens/config";

function mountConfig() {
  return render(
    <MemoryRouter initialEntries={["/config"]}>
      <AuthProvider><Config /></AuthProvider>
    </MemoryRouter>,
  );
}

// Load the editor and switch to the NET/ROM + INP3 tab.
async function openNetRomTab() {
  mountConfig();
  await waitFor(() => expect(screen.getAllByText(/Identity/i).length).toBeGreaterThan(0));
  fireEvent.click(screen.getByRole("button", { name: "NET/ROM + INP3" }));
  await waitFor(() => expect(screen.getByText("Routing role")).toBeInTheDocument());
}

describe("the NET/ROM section speaks the server's config dialect", () => {
  it("renders the routing picker on the string value the server sends", async () => {
    await openNetRomTab();

    // The mock config carries routing: "Transit" (what GET /config now emits). The
    // picker must select that option - with an integer wire it fell back to the first.
    const select = screen.getByRole("combobox") as HTMLSelectElement;
    expect(select.value).toBe("Transit");
    expect(screen.getByRole("option", { name: "Full router", selected: true })).toBeInTheDocument();
  });

  it("changing the routing role keeps a member name on the draft, not an index", async () => {
    await openNetRomTab();

    const select = screen.getByRole("combobox") as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "Endpoint" } });

    // The <option> values ARE the wire strings, so the draft (and therefore the PUT
    // body, which is JSON.stringify of the draft) carries "Endpoint".
    expect(select.value).toBe("Endpoint");
    expect(["None", "Endpoint", "Transit"]).toContain(select.value);
  });

  it("edits the four INP3 timers as numbers of seconds", async () => {
    await openNetRomTab();

    fireEvent.click(screen.getByRole("button", { name: /INP3 timing intervals/i }));
    await waitFor(() => expect(screen.getByText("Time-probe interval")).toBeInTheDocument());

    // Server defaults, in seconds: 60 / 180 / 300 / 5.
    const numbers = screen.getAllByRole("spinbutton") as HTMLInputElement[];
    expect(numbers.map((n) => n.value)).toEqual(["60", "180", "300", "5"]);

    // Every one of the four is labelled in seconds - no "probes", no duration text.
    expect(screen.getAllByText("seconds")).toHaveLength(4);

    // And an edit stays numeric (the server binds a JSON number to the TimeSpan).
    fireEvent.change(numbers[0], { target: { value: "120" } });
    expect(numbers[0].value).toBe("120");
  });
});
