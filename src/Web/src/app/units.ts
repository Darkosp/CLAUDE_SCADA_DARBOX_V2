import { Unit } from './models';

/**
 * Units an operator can pick when configuring a numeric tag.
 *
 * The picker exists so a unit is never typed as free text: ADR-0005 makes a unit a
 * dimension plus its conversion to SI, and a text box would let someone enter "bar" with
 * nothing behind it to convert or compare with. Adding a unit here is deliberate work,
 * which is the intent.
 *
 * Temperatures carry an offset as well as a factor — a factor-only conversion puts 0 °C
 * at 0 °F.
 */
export const UNIT_PRESETS: readonly Unit[] = [
  { symbol: 'bar', dimension: 'Pressure', factorToSi: 100_000, offsetToSi: 0 },
  { symbol: 'kPa', dimension: 'Pressure', factorToSi: 1_000, offsetToSi: 0 },
  { symbol: 'psi', dimension: 'Pressure', factorToSi: 6_894.757_293_168_36, offsetToSi: 0 },
  { symbol: '°C', dimension: 'Temperature', factorToSi: 1, offsetToSi: 273.15 },
  { symbol: '°F', dimension: 'Temperature', factorToSi: 5 / 9, offsetToSi: 255.372_222_222_222 },
  { symbol: 'm³/h', dimension: 'VolumeFlow', factorToSi: 1 / 3600, offsetToSi: 0 },
  { symbol: 'l/s', dimension: 'VolumeFlow', factorToSi: 0.001, offsetToSi: 0 },
  { symbol: 'kW', dimension: 'Power', factorToSi: 1_000, offsetToSi: 0 },
  { symbol: 'A', dimension: 'ElectricCurrent', factorToSi: 1, offsetToSi: 0 },
  { symbol: 'V', dimension: 'ElectricPotential', factorToSi: 1, offsetToSi: 0 },
  { symbol: 'Hz', dimension: 'Frequency', factorToSi: 1, offsetToSi: 0 },
  { symbol: 'm', dimension: 'Length', factorToSi: 1, offsetToSi: 0 },
];

export function unitBySymbol(symbol: string): Unit | null {
  return UNIT_PRESETS.find((unit) => unit.symbol === symbol) ?? null;
}
