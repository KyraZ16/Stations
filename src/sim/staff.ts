const FIRST = [
  'Anna', 'Ben', 'Clara', 'Deniz', 'Elif', 'Felix', 'Greta', 'Hannes', 'Ida', 'Jonas', 'Katrin', 'Lukas',
  'Mia', 'Nico', 'Olga', 'Paul', 'Rosa', 'Sami', 'Tina', 'Uwe', 'Vera', 'Willi', 'Yusuf', 'Zoe', 'Marek',
  'Leonie', 'Tobias', 'Sabine', 'Ahmet', 'Jana', 'Frank', 'Ines', 'Kai', 'Nadine', 'Ole', 'Petra',
];
const LAST = [
  'Müller', 'Schmidt', 'Schneider', 'Fischer', 'Weber', 'Meyer', 'Wagner', 'Becker', 'Schulz', 'Hoffmann',
  'Koch', 'Richter', 'Klein', 'Wolf', 'Neumann', 'Schwarz', 'Braun', 'Krüger', 'Hartmann', 'Lange', 'Yılmaz',
  'Kowalski', 'Werner', 'Krause', 'Lehmann', 'Köhler', 'Peters', 'Jansen', 'Petersen', 'Arslan', 'Nowak',
];

export function randomName(): string {
  const f = FIRST[Math.floor(Math.random() * FIRST.length)];
  const l = LAST[Math.floor(Math.random() * LAST.length)];
  return `${f} ${l}`;
}
