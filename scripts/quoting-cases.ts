/**
 * @file
 *
 * Prints, as JSON, the line every quoting model generates for every corpus case, and the arguments the model
 * says the program receives from it under this process's environment. `scripts/test-quoting-models.ps1` runs
 * each line through the real caller and compares.
 */

import { CALLERS, CORPUS, FORWARD_CORPUS, getCaller } from '../docs/src/quoting-models/quoting.ts';

interface QuotingCase {
  readonly caller: string;
  readonly error: null | string;
  readonly expected: readonly string[] | null;
  readonly line: null | string;
  readonly marker: string;
}

const CODE_CALLERS = new Set(['csharp', 'node', 'python']);
const environment = new Map<string, string>();
for (const [name, value] of Object.entries(process.env)) {
  if (value !== undefined) {
    environment.set(name, value);
  }
}

const cases: QuotingCase[] = [];
for (const caller of CALLERS) {
  CORPUS.forEach((args, index) => {
    const marker = `case-${String(index)}`;
    const wanted = [marker, ...args];
    const reversed = caller.reverse(wanted);
    if (reversed.line === undefined) {
      cases.push({ caller: caller.id, error: reversed.error ?? 'no line', expected: null, line: null, marker });
      return;
    }
    const expected = CODE_CALLERS.has(caller.id) ? wanted : caller.forward(reversed.line, environment).args ?? null;
    cases.push({ caller: caller.id, error: null, expected, line: reversed.line, marker });
  });
}

FORWARD_CORPUS.forEach(({ caller, line }, index) => {
  const marker = `typed-${String(index)}`;
  const markedLine = `${marker} ${line}`;
  const forward = getCaller(caller).forward(markedLine, environment);
  cases.push({ caller, error: forward.error ?? null, expected: forward.args ?? null, line: forward.error === undefined ? markedLine : null, marker });
});

process.stdout.write(JSON.stringify(cases));
