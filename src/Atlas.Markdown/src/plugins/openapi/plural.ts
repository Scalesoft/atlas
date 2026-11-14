/** Czech plural form: 1 → one, 2–4 → few, otherwise many (0, 5+). */
export const czechPlural = (count: number, one: string, few: string, many: string) => count === 1 ? one : count >= 2 && count <= 4 ? few : many
export const operationsCount = (count: number) => `${count} ${czechPlural(count, 'operace', 'operace', 'operací')}`
export const schemasCount = (count: number) => `${count} ${czechPlural(count, 'schéma', 'schémata', 'schémat')}`
