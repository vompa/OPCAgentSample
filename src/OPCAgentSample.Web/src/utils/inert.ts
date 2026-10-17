/**
 * Attribute, die einen Bereich inert machen (nicht fokussierbar, nicht bedienbar, für Hilfstechnik verborgen).
 * Als Attribut ohne Wert statt `:inert="false"`: Ältere Umgebungen kennen die Eigenschaft nicht und läsen „false“
 * sonst als gesetztes Attribut.
 */
export function inertAttrs(inert: boolean): Record<string, unknown> {
  return inert ? { inert: '' } : {};
}
