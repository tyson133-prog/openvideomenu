export const STYLES = `
.ms-root { position: relative; width: 100%; height: 100%; overflow: hidden; background: #000; color: #fff;
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; user-select: none; outline: none; }
.ms-stage { position: absolute; overflow: hidden; background: #000; }
.ms-bg { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: fill; pointer-events: none; }
.ms-overlay { position: absolute; pointer-events: none; object-fit: fill; }
.ms-button { position: absolute; box-sizing: border-box; display: flex; align-items: center; justify-content: center;
  cursor: pointer; border-radius: 0.2em; outline: none; line-height: 1.1; text-align: center; white-space: nowrap; }
.ms-button.ms-labelled { color: rgba(255,255,255,.85); text-shadow: 0 1px 3px rgba(0,0,0,.8); }
.ms-button.ms-labelled.ms-selected { color: #fff; }
.ms-button.ms-ring.ms-selected { box-shadow: 0 0 0 3px #ffd54a, 0 0 18px 4px rgba(255,213,74,.6); }
.ms-button.ms-ring.ms-activated { box-shadow: 0 0 0 3px #fff, 0 0 24px 8px rgba(255,255,255,.8); }
.ms-stage.ms-intro .ms-button, .ms-stage.ms-intro .ms-overlay { visibility: hidden; }
.ms-transition { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; background: #000; z-index: 5; }
.ms-video { position: absolute; inset: 0; width: 100%; height: 100%; background: #000; z-index: 10; outline: none; }
.ms-toast { position: absolute; left: 50%; bottom: 6%; transform: translateX(-50%); z-index: 20; max-width: 80%;
  padding: .7em 1.2em; border-radius: .5em; background: rgba(20,20,20,.9); font-size: clamp(14px, 2.2vh, 28px);
  transition: opacity .3s; pointer-events: none; }
.ms-fallback { position: absolute; inset: 0; overflow: auto; padding: 6vh 8vw; box-sizing: border-box;
  background: linear-gradient(#15151b, #000); font-size: clamp(16px, 2.6vh, 34px); }
.ms-fallback h1 { margin: 0 0 .8em; font-size: 1.8em; font-weight: 600; }
.ms-fallback h2 { margin: 1.2em 0 .4em; font-size: 1em; font-weight: 500; opacity: .6; text-transform: uppercase; letter-spacing: .08em; }
.ms-fallback button { display: block; width: 100%; max-width: 32em; margin: .2em 0; padding: .5em .8em; text-align: left;
  font: inherit; color: inherit; background: rgba(255,255,255,.06); border: 0; border-radius: .3em; cursor: pointer; outline: none; }
.ms-fallback button.ms-selected, .ms-fallback button:focus-visible { background: #ffd54a; color: #000; }
`;

let injected = false;
export function injectStyles(doc: Document = document) {
  if (injected) return;
  const style = doc.createElement("style");
  style.dataset.openvideomenu = "";
  style.textContent = STYLES;
  doc.head.appendChild(style);
  injected = true;
}
