import {useEffect, useState} from "react";

/**
 * Browser connectivity hint, not proof the API or identity provider is up.
 * The PWA never queues offline writes; server authentication is mandatory
 * on every reconnect before protected data may be shown again.
 */
export function useOnlineStatus(): boolean {
  const [online, setOnline] = useState(() =>
    typeof navigator === "undefined" ? true : navigator.onLine
  );
  useEffect(() => {
    const sync = () => setOnline(navigator.onLine);
    window.addEventListener("online", sync);
    window.addEventListener("offline", sync);
    sync();
    return () => {
      window.removeEventListener("online", sync);
      window.removeEventListener("offline", sync);
    };
  }, []);
  return online;
}
