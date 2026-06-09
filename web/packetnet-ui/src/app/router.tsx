import { createBrowserRouter, Navigate } from "react-router-dom";
import { Shell } from "@/components/layout/shell";
import { useAuth } from "@/app/auth";
import { Login } from "@/screens/login";
import { Setup } from "@/screens/setup";
import { Dashboard } from "@/screens/dashboard";
import { Monitor } from "@/screens/monitor";
import { Sessions } from "@/screens/sessions";
import { Routes } from "@/screens/routes";
import { Ports } from "@/screens/ports";
import { Config } from "@/screens/config";
import { Users } from "@/screens/users";
import { LinkTuner } from "@/screens/link-tuner";

function RequireAuth() {
  const { authed } = useAuth();
  return authed ? <Shell /> : <Navigate to="/login" replace />;
}

// Bounce an already-authed user away from the auth screens.
function AuthOnly({ children }: { children: React.ReactNode }) {
  const { authed } = useAuth();
  return authed ? <Navigate to="/" replace /> : <>{children}</>;
}

export const router = createBrowserRouter([
  { path: "/login", element: <AuthOnly><Login /></AuthOnly> },
  { path: "/setup", element: <Setup /> },
  {
    path: "/",
    element: <RequireAuth />,
    children: [
      { index: true, element: <Dashboard /> },
      { path: "monitor", element: <Monitor /> },
      { path: "sessions", element: <Sessions /> },
      { path: "routes", element: <Routes /> },
      { path: "ports", element: <Ports /> },
      { path: "config", element: <Config /> },
      { path: "users", element: <Users /> },
      { path: "tools/tuner", element: <LinkTuner /> },
      { path: "*", element: <Navigate to="/" replace /> },
    ],
  },
]);
