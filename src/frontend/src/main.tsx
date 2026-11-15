import React from "react";
import ReactDOM from "react-dom/client";
import { AuthProvider } from "react-oidc-context";
import App from "./App";
import "./i18n";
import "./styles.css";
ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode><AuthProvider
    authority={import.meta.env.VITE_OIDC_AUTHORITY || "http://keycloak.localhost:8081/realms/peritial"}
    client_id={import.meta.env.VITE_OIDC_CLIENT || "peritial-web"}
    redirect_uri={window.location.origin + "/"}
    response_type="code"
    scope="openid profile email peritial.read peritial.write"
    onSigninCallback={() => window.history.replaceState({}, document.title, window.location.pathname)}
  ><App/></AuthProvider></React.StrictMode>
);