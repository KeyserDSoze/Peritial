import i18next from "i18next";
import { initReactI18next } from "react-i18next";
i18next.use(initReactI18next).init({
  lng: "it", fallbackLng: "en", interpolation: { escapeValue: false },
  resources: {
    it: { translation: { title:"Peritial", subtitle:"Gestione perizie e documenti", signIn:"Accedi", signOut:"Esci", theme:"Cambia tema", language:"Lingua", welcome:"Gestione perizie", signInHelp:"Accedi con il tuo account aziendale per gestire le pratiche.", perizie:"Perizie", reference:"Riferimento", newPerizia:"Nuova perizia", create:"Crea", upload:"Carica documento", file:"Documento", status:"Stato", empty:"Nessuna perizia presente.", loading:"Caricamento…", error:"Impossibile completare l'operazione.", tenant:"Accesso protetto per organizzazione" } },
    en: { translation: { title:"Peritial", subtitle:"Appraisals and documents", signIn:"Sign in", signOut:"Sign out", theme:"Toggle theme", language:"Language", welcome:"Appraisal management", signInHelp:"Sign in with your organisation account to manage cases.", perizie:"Appraisals", reference:"Reference", newPerizia:"New appraisal", create:"Create", upload:"Upload document", file:"Document", status:"Status", empty:"No appraisals yet.", loading:"Loading…", error:"Unable to complete the operation.", tenant:"Organisation-protected access" } }
  }
});
export default i18next;