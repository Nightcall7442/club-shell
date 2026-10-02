/**
 * Inserting text at the caret of a (possibly React-controlled) input or textarea: the chat's quick replies and emoji.
 */
export type TextTarget = HTMLInputElement | HTMLTextAreaElement;

function setNativeValue(el: TextTarget, value: string): void {
  const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
  if (setter) {
    setter.call(el, value);
  } else {
    el.value = value;
  }
  el.dispatchEvent(new Event('input', { bubbles: true }));
}

/** Inserts `text` at the caret (replacing any selection) and fires `input` so React sees the change. */
export function insertText(el: TextTarget, text: string): void {
  const start = el.selectionStart ?? el.value.length;
  const end = el.selectionEnd ?? el.value.length;
  const v = el.value;
  setNativeValue(el, v.slice(0, start) + text + v.slice(end));
  try {
    el.setSelectionRange(start + text.length, start + text.length);
  } catch {
    // input types without selection support (number, email): caret stays at the end
  }
}
