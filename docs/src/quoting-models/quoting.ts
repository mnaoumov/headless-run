/**
 * @file
 *
 * The quoting models of the playground page: for each caller, how a typed line becomes the argument list a
 * Windows program receives (forward), and the line to type for a wanted argument list (reverse).
 *
 * The Windows split is exact: it is the Microsoft C runtime's rule, the same one `headless-run --echo` applies.
 * The shell layers above it are models. `scripts/test-quoting-models.ps1` runs every line they generate for the
 * corpus below through the real caller and checks what the program receives.
 */

/** What a caller turns a typed line into. */
export interface ForwardResult {
  /** The arguments the program receives, when the model can tell. */
  readonly args?: readonly string[];
  /** Why the model cannot tell, when it cannot. */
  readonly error?: string;
  /** What the person should know about the line: expansions, operators, limits. */
  readonly notes: readonly string[];
}

/** The line to write in a caller for a wanted argument list. */
export interface ReverseResult {
  /** Why no line can carry these arguments in this caller. */
  readonly error?: string;
  /** The text to write after the program name. */
  readonly line?: string;
  /** What the person should know about the line: limits, expansions that cannot be escaped. */
  readonly notes: readonly string[];
}

/** One place a Windows program can be started from. */
export interface Caller {
  /** The program typed into the caller, for display in front of the line. */
  readonly example: string;
  /** Turns the text written after the program name into the arguments the program receives. */
  forward(line: string, environment?: Environment): ForwardResult;
  /** A stable id, used by the page and the tests. */
  readonly id: string;
  /** The caller's name as the page shows it. */
  readonly name: string;
  /** Turns a wanted argument list into the text to write after the program name. */
  reverse(args: readonly string[]): ReverseResult;
  /** How the model is checked against the real caller. */
  readonly verified: string;
}

/** Environment variables a caller may expand, by name; a name not listed is unset. */
export type Environment = ReadonlyMap<string, string>;

const NO_ENVIRONMENT: Environment = new Map<string, string>();
const SHORTCUT_ARGUMENTS_LIMIT = 1023;
const RUN_DIALOG_LIMIT = 259;

/**
 * Splits the arguments part of a Windows command line as the Microsoft C runtime does: space and tab
 * separate, `"` toggles quoting, `""` inside quotes is a literal quote, and backslashes are literal except before
 * a quote, where `2n` of them give `n` and open or close quoting, and `2n+1` give `n` and a literal quote.
 * An unclosed quote runs to the end of the line.
 */
export function splitWindowsArguments(line: string): string[] {
  const args: string[] = [];
  let position = 0;
  while (true) {
    while (position < line.length && isWindowsSpace(line[position])) {
      position++;
    }
    if (position === line.length) {
      return args;
    }
    let value = '';
    let quoted = false;
    while (position < line.length && (quoted || !isWindowsSpace(line[position]))) {
      let slashes = 0;
      while (line[position] === '\\') {
        slashes++;
        position++;
      }
      if (line[position] === '"') {
        value += '\\'.repeat(Math.floor(slashes / 2));
        if (slashes % 2 === 1) {
          value += '"';
        } else if (quoted && line[position + 1] === '"') {
          value += '"';
          position++;
        } else {
          quoted = !quoted;
        }
        position++;
      } else {
        value += '\\'.repeat(slashes);
        if (position < line.length && (quoted || !isWindowsSpace(line[position]))) {
          value += line[position];
          position++;
        }
      }
    }
    args.push(value);
  }
}

/**
 * Quotes one argument by Windows' rules, as .NET's `ArgumentList` and PowerShell 7 do: left bare when it has no
 * space, tab or quote, otherwise wrapped in quotes with `\"` for an embedded quote and backslashes doubled before
 * a quote and at the end.
 */
export function quoteWindowsArgument(arg: string): string {
  if (arg !== '' && !/[ \t"]/u.test(arg)) {
    return arg;
  }
  return `"${escapeWindowsQuoted(arg)}"`;
}

/** Joins arguments into the arguments part of a Windows command line. */
export function joinWindowsArguments(args: readonly string[]): string {
  return args.map(quoteWindowsArgument).join(' ');
}

/** The text between the quotes {@link quoteWindowsArgument} adds. */
function escapeWindowsQuoted(arg: string): string {
  let result = '';
  let slashes = 0;
  for (const character of arg) {
    if (character === '\\') {
      slashes++;
      continue;
    }
    result += '\\'.repeat(character === '"' ? slashes * 2 + 1 : slashes) + character;
    slashes = 0;
  }
  return result + '\\'.repeat(slashes * 2);
}

function isWindowsSpace(character: string | undefined): boolean {
  return character === ' ' || character === '\t';
}

function lineBreakError(args: readonly string[], caller: string): null | string {
  return args.some((arg) => /[\r\n]/u.test(arg)) ? `${caller} cannot pass a line break inside an argument.` : null;
}

function nulError(args: readonly string[]): null | string {
  return args.some((arg) => arg.includes('\0')) ? 'No Windows command line can carry a NUL character.' : null;
}

// ---------------------------------------------------------------------------------------------------------------
// The raw command line: CreateProcess, a shortcut, Task Scheduler and the Run dialog.
// ---------------------------------------------------------------------------------------------------------------

/** `%NAME%` spans a caller that calls `ExpandEnvironmentStrings` would replace if `NAME` were set. */
function percentSpans(line: string): string[] {
  return [...line.matchAll(/%[^%\r\n]+%/gu)].map((match) => match[0]);
}

/** Replaces each `%NAME%` whose `NAME` is set, as `ExpandEnvironmentStrings` does, leaving the rest as written. */
function expandEnvironmentStrings(line: string, environment: Environment): string {
  let result = '';
  let position = 0;
  while (position < line.length) {
    const start = line.indexOf('%', position);
    if (start < 0) {
      break;
    }
    const end = line.indexOf('%', start + 1);
    if (end < 0) {
      break;
    }
    const value = lookup(environment, line.slice(start + 1, end));
    if (value === null) {
      result += line.slice(position, end);
      position = end;
    } else {
      result += line.slice(position, start) + value;
      position = end + 1;
    }
  }
  return result + line.slice(position);
}

function lookup(environment: Environment, name: string): null | string {
  for (const [key, value] of environment) {
    if (key.toUpperCase() === name.toUpperCase()) {
      return value;
    }
  }
  return null;
}

interface RawOptions {
  readonly example: string;
  readonly shouldExpand: boolean;
  readonly id: string;
  readonly limit?: { readonly characters: number; readonly what: string };
  readonly name: string;
  readonly verified: string;
}

function rawCaller(options: RawOptions): Caller {
  return {
    example: options.example,
    forward(line, environment = NO_ENVIRONMENT): ForwardResult {
      const notes: string[] = [];
      let expanded = line;
      if (options.shouldExpand) {
        expanded = expandEnvironmentStrings(line, environment);
        for (const span of percentSpans(line)) {
          notes.push(`${span} is replaced by the variable's value when it is set; shown as written when it is not.`);
        }
      }
      if (options.id === 'shortcut' && /[^\u0000-ÿ]/u.test(line)) {
        notes.push('A shortcut saved by WScript.Shell\'s CreateShortcut can store characters outside the ANSI code page as ?: check the shortcut with headless-run --echo.');
      }
      if (options.limit && line.length > options.limit.characters) {
        notes.push(`${options.limit.what} holds at most ${String(options.limit.characters)} characters; the rest is cut with no error.`);
      }
      return { args: splitWindowsArguments(expanded), notes };
    },
    id: options.id,
    name: options.name,
    reverse(args): ReverseResult {
      const error = nulError(args);
      if (error) {
        return { error, notes: [] };
      }
      const line = joinWindowsArguments(args);
      const notes: string[] = [];
      if (options.shouldExpand) {
        for (const span of percentSpans(line)) {
          notes.push(`${span} cannot be escaped here: it reaches the program as the variable's value whenever that variable is set.`);
        }
      }
      if (options.id === 'shortcut' && /[^\u0000-ÿ]/u.test(line)) {
        notes.push('A shortcut saved by WScript.Shell\'s CreateShortcut can store characters outside the ANSI code page as ?: check the shortcut with headless-run --echo.');
      }
      if (options.limit && line.length > options.limit.characters) {
        notes.push(`${options.limit.what} holds at most ${String(options.limit.characters)} characters, and this line has ${String(line.length)}: put the call in a .cmd file.`);
      }
      return { line, notes };
    },
    verified: options.verified
  };
}

// ---------------------------------------------------------------------------------------------------------------
// cmd.exe: the prompt and a .cmd or .bat file.
// ---------------------------------------------------------------------------------------------------------------

const CMD_OPERATORS = new Set(['&', '<', '>', '|']);
const CMD_ESCAPED = new Set(['&', '(', ')', '<', '>', '^', '|']);

/** cmd's percent phase at the prompt: a set `%NAME%` is replaced, anything else stays as typed. */
function expandPromptPercent(line: string, environment: Environment, notes: string[]): string {
  for (const span of percentSpans(line)) {
    if (lookup(environment, span.slice(1, -1)) === null) {
      notes.push(`${span} stays as typed while ${span.slice(1, -1)} is unset, and is replaced by its value when it is set.`);
    }
  }
  return expandEnvironmentStrings(line, environment);
}

/** cmd's percent phase in a batch file: `%%` is a `%`, `%NAME%` is the value or nothing, a lone `%` is dropped. */
function expandBatchPercent(line: string, environment: Environment, notes: string[]): string {
  let result = '';
  let position = 0;
  while (position < line.length) {
    const character = line[position];
    if (character !== '%') {
      result += character;
      position++;
      continue;
    }
    if (line[position + 1] === '%') {
      result += '%';
      position += 2;
      continue;
    }
    const next = line[position + 1] ?? '';
    if (/[0-9*]/u.test(next)) {
      notes.push(`%${next} is a batch parameter: it is replaced by the batch file's own arguments, empty here.`);
      position += 2;
      continue;
    }
    const end = line.indexOf('%', position + 1);
    if (end < 0) {
      notes.push('A lone % is dropped in a batch file: write %% for a literal %.');
      position++;
      continue;
    }
    const name = line.slice(position + 1, end);
    const value = lookup(environment, name);
    if (value === null) {
      notes.push(`%${name}% is empty while ${name} is unset.`);
    }
    result += value ?? '';
    position = end + 1;
  }
  return result;
}

/**
 * cmd's second phase: `^` escapes the next character outside quotes, `"` toggles quoting, and an unescaped `&`,
 * `|`, `<` or `>` outside quotes ends the command. What is left is passed to the program as written.
 */
function applyCmdCarets(line: string, notes: string[]): string {
  let result = '';
  let quoted = false;
  for (let position = 0; position < line.length; position++) {
    const character = line[position];
    if (character === '"') {
      quoted = !quoted;
      result += character;
    } else if (!quoted && character === '^') {
      position++;
      if (position < line.length) {
        result += line[position];
      } else {
        notes.push('A ^ at the end of the line continues the command on the next line.');
      }
    } else if (!quoted && CMD_OPERATORS.has(character)) {
      notes.push(`The unquoted ${character} is a cmd operator: the program receives only the text before it, and cmd runs or redirects the rest.`);
      return result;
    } else {
      result += character;
    }
  }
  return result;
}

/**
 * Escapes a Windows command line for cmd. Outside cmd's own quotes, operators get `^` and a `%` gets `^` after it
 * at the prompt (so no `%NAME%` can match), or is doubled in a batch file. When a prompt `%` would land inside
 * cmd's quotes, where `^` is literal, every quote is written `^"` instead, so cmd never enters quotes at all.
 */
function escapeForCmd(line: string, isBatch: boolean, shouldEscapeEveryQuote: boolean): string {
  let result = '';
  let quoted = false;
  for (let position = 0; position < line.length; position++) {
    const character = line[position];
    const next = line[position + 1];
    if (character === '"') {
      if (shouldEscapeEveryQuote) {
        result += '^"';
      } else {
        quoted = !quoted;
        result += '"';
      }
    } else if (character === '%') {
      if (isBatch) {
        result += '%%';
      } else if (quoted || next === undefined || next === '"' || CMD_ESCAPED.has(next) || !line.includes('%', position + 1)) {
        // With no later %, this one cannot open a %NAME%, so it needs no ^.
        result += '%';
      } else {
        result += '%^';
      }
    } else if (!quoted && CMD_ESCAPED.has(character)) {
      result += `^${character}`;
    } else {
      result += character;
    }
  }
  return result;
}

/** Whether a prompt `%` lands inside cmd's quotes in a line with another `%` it could pair with. */
function detectPromptPercentInsideQuotes(line: string): boolean {
  if ((line.match(/%/gu) ?? []).length < 2) {
    return false;
  }
  let quoted = false;
  for (const character of line) {
    if (character === '"') {
      quoted = !quoted;
    } else if (character === '%' && quoted) {
      return true;
    }
  }
  return false;
}

function cmdCaller(isBatch: boolean): Caller {
  const name = isBatch ? 'cmd.exe: a .cmd or .bat file' : 'cmd.exe: the prompt';
  return {
    example: 'tool.exe',
    forward(line, environment = NO_ENVIRONMENT): ForwardResult {
      const notes: string[] = [];
      const expanded = isBatch ? expandBatchPercent(line, environment, notes) : expandPromptPercent(line, environment, notes);
      const passed = applyCmdCarets(expanded, notes);
      if (/!/u.test(passed)) {
        notes.push('! is literal while delayed expansion is off, the default; under cmd /v:on or setlocal EnableDelayedExpansion, !NAME! expands and a lone ! is dropped.');
      }
      return { args: splitWindowsArguments(passed), notes };
    },
    id: isBatch ? 'cmd-batch' : 'cmd-prompt',
    name,
    reverse(args): ReverseResult {
      const error = nulError(args) ?? lineBreakError(args, 'cmd');
      if (error) {
        return { error, notes: [] };
      }
      const windowsLine = joinWindowsArguments(args);
      const shouldEscapeEveryQuote = !isBatch && detectPromptPercentInsideQuotes(windowsLine);
      const line = escapeForCmd(windowsLine, isBatch, shouldEscapeEveryQuote);
      const notes: string[] = [];
      if (shouldEscapeEveryQuote) {
        notes.push('A % would sit inside cmd\'s quotes, where it cannot be escaped, so every " is written ^" and cmd never enters quotes.');
      }
      if (args.some((arg) => arg.includes('!'))) {
        notes.push('This assumes delayed expansion is off, the default. Under cmd /v:on, ! cannot be passed reliably.');
      }
      if (isBatch && /[^\u0000-\u007F]/u.test(line)) {
        notes.push('Save the file as UTF-8 and put chcp 65001 >nul before this line, or the characters outside ASCII are read in the console code page.');
      }
      if (!isBatch) {
        notes.push('The prompt does not wait for a Windows-subsystem program: put start "" /wait in front of one.');
      }
      return { line, notes };
    },
    verified: isBatch ? 'Run from a .cmd file through cmd /d /c, for every corpus case.' : 'Typed into cmd /d /q through its standard input, which cmd parses as the prompt does, for every corpus case.'
  };
}

// ---------------------------------------------------------------------------------------------------------------
// PowerShell: version 7.3 and later, and Windows PowerShell 5.1.
// ---------------------------------------------------------------------------------------------------------------

interface ParsedWords {
  readonly error?: string;
  readonly words: readonly string[];
}

/**
 * Reads PowerShell's argument mode for a native command: bare words, `'single'` (with `''` for a quote),
 * `"double"` (with backtick escapes and `""`), and a backtick escape outside quotes. Constructs that need the
 * real engine (variables, subexpressions, operators, arrays, splatting) are reported, not guessed.
 */
function readPowerShellWords(line: string, notes: string[]): ParsedWords {
  const words: string[] = [];
  let position = 0;
  while (true) {
    while (position < line.length && /\s/u.test(line[position])) {
      position++;
    }
    if (position === line.length) {
      return { words };
    }
    if (line.startsWith('--%', position) && (position + 3 === line.length || /\s/u.test(line[position + 3]))) {
      return { error: 'stop-parsing', words };
    }
    if (line[position] === '#') {
      notes.push('# at the start of a word begins a comment: the rest of the line is ignored.');
      return { words };
    }
    let value = '';
    while (position < line.length && !/\s/u.test(line[position])) {
      const character = line[position];
      if (character === '\'') {
        const end = findSingleQuoteEnd(line, position + 1);
        if (end < 0) {
          return { error: 'The single quote is not closed.', words };
        }
        value += line.slice(position + 1, end).replaceAll('\'\'', '\'');
        position = end + 1;
      } else if (character === '"') {
        const read = readPowerShellDouble(line, position + 1, notes);
        if (read.error !== undefined) {
          return { error: read.error, words };
        }
        value += read.value;
        position = read.end + 1;
      } else if (character === '`') {
        if (position + 1 === line.length) {
          return { error: 'A backtick at the end of the line continues the command on the next line.', words };
        }
        value += line[position + 1];
        position += 2;
      } else if (character === '$') {
        return { error: `$ outside single quotes starts a variable or an expression, which this model does not evaluate: put the word in single quotes.`, words };
      } else if (/[;|&<>(){}@,]/u.test(character)) {
        return { error: `The unquoted ${character} is PowerShell syntax, which this model does not evaluate: put the word in single quotes.`, words };
      } else {
        value += character;
        position++;
      }
    }
    words.push(value);
  }
}

function findSingleQuoteEnd(line: string, from: number): number {
  let position = from;
  while (position < line.length) {
    if (line[position] === '\'') {
      if (line[position + 1] === '\'') {
        position += 2;
        continue;
      }
      return position;
    }
    position++;
  }
  return -1;
}

const POWERSHELL_ESCAPES: ReadonlyMap<string, string> = new Map([['0', '\0'], ['a', '\u0007'], ['b', '\b'], ['e', '\u001B'], ['f', '\f'], ['n', '\n'], ['r', '\r'], ['t', '\t'], ['v', '\v']]);

function readPowerShellDouble(line: string, from: number, notes: string[]): { end: number; error?: string; value: string } {
  let value = '';
  let position = from;
  while (position < line.length) {
    const character = line[position];
    if (character === '`') {
      const next = line[position + 1] ?? '';
      value += POWERSHELL_ESCAPES.get(next) ?? next;
      position += 2;
    } else if (character === '"') {
      if (line[position + 1] === '"') {
        value += '"';
        position += 2;
        continue;
      }
      return { end: position, value };
    } else if (character === '$' && /[\w{(:?^$]/u.test(line[position + 1] ?? '')) {
      return { end: position, error: '$ inside double quotes expands a variable or an expression, which this model does not evaluate: use single quotes.', value };
    } else {
      value += character;
      position++;
    }
  }
  notes.push('The double quote is not closed.');
  return { end: position, error: 'The double quote is not closed.', value };
}

const POWERSHELL_BARE = /^(?:[\w./:\\=+%][\w./:\\=+%-]*|-+[\w-]*)$/u;

/** A PowerShell literal for one value: bare when nothing in it is special, otherwise single-quoted. */
function powerShellLiteral(value: string): string {
  if (POWERSHELL_BARE.test(value) && value !== '--%') {
    return value;
  }
  return `'${value.replaceAll('\'', '\'\'')}'`;
}

/** Windows PowerShell 5.1's rule for a native argument: wrapped in quotes when it has whitespace outside its own quotes. */
function checkLegacyNeedsQuotes(value: string): boolean {
  let quotes = 0;
  for (const character of value) {
    if (character === '"') {
      quotes++;
    } else if (/\s/u.test(character) && quotes % 2 === 0) {
      return true;
    }
  }
  return false;
}

/** The command line Windows PowerShell 5.1 builds from the values: empty values are dropped, nothing is escaped. */
function legacyCommandLine(values: readonly string[]): string {
  return values.filter((value) => value !== '').map((value) => (checkLegacyNeedsQuotes(value) ? `"${value}"` : value)).join(' ');
}

/**
 * The text after a stop-parsing token. Windows PowerShell 5.1 passes it as a raw command line. PowerShell 7.3 and
 * later split it at whitespace and quote each piece by Windows' rules, so a quote in it reaches the program as a
 * literal character (checked on Windows 11 with PowerShell 7.6).
 */
function splitStopParsingText(text: string, isLegacy: boolean): string[] {
  return isLegacy ? splitWindowsArguments(text) : text.split(/\s+/u).filter((piece) => piece !== '');
}

function stopParsing(line: string, environment: Environment, notes: string[], isLegacy: boolean): ForwardResult {
  const rest = line.slice(line.indexOf('--%') + 3).replace(/^\s+/u, '');
  notes.push(isLegacy
    ? 'After --%, the rest of the line goes to the program as a raw command line, with %NAME% expanded when NAME is set.'
    : 'After --%, the rest of the line is split at whitespace and each piece passed as it is, quotes included, with %NAME% expanded when NAME is set.');
  for (const span of percentSpans(rest)) {
    if (lookup(environment, span.slice(1, -1)) === null) {
      notes.push(`${span} stays as typed while ${span.slice(1, -1)} is unset.`);
    }
  }
  return { args: splitStopParsingText(expandEnvironmentStrings(rest, environment), isLegacy), notes };
}

function powerShellCaller(isLegacy: boolean): Caller {
  return {
    example: 'tool.exe',
    forward(line, environment = NO_ENVIRONMENT): ForwardResult {
      const notes: string[] = [];
      const parsed = readPowerShellWords(line, notes);
      if (parsed.error === 'stop-parsing') {
        const before = parsed.words;
        const after = stopParsing(line, environment, notes, isLegacy);
        const head = isLegacy ? splitWindowsArguments(legacyCommandLine(before)) : before;
        return { args: [...head, ...(after.args ?? [])], notes };
      }
      if (parsed.error !== undefined) {
        return { error: parsed.error, notes };
      }
      const stop = parsed.words.indexOf('--%');
      if (stop >= 0) {
        // Checked on Windows 11: even a quoted '--%' or ('--%') stops parsing, and the values after it are joined as they are.
        notes.push('A --% value stops parsing however it is written: the values after it are joined with spaces and read as the text after --% is.');
        const head = parsed.words.slice(0, stop);
        const tail = splitStopParsingText(parsed.words.slice(stop + 1).join(' '), isLegacy);
        return { args: [...(isLegacy ? splitWindowsArguments(legacyCommandLine(head)) : head), ...tail], notes };
      }
      if (!isLegacy) {
        return { args: parsed.words, notes };
      }
      if (parsed.words.includes('')) {
        notes.push('Windows PowerShell 5.1 drops an empty argument: write \'""\' to pass one.');
      }
      const commandLine = legacyCommandLine(parsed.words);
      notes.push(`Windows PowerShell 5.1 builds this command line, escaping nothing: ${commandLine}`);
      return { args: splitWindowsArguments(commandLine), notes };
    },
    id: isLegacy ? 'powershell' : 'pwsh',
    name: isLegacy ? 'Windows PowerShell 5.1' : 'PowerShell 7.3 and later',
    reverse(args): ReverseResult {
      const error = nulError(args);
      if (error) {
        return { error, notes: [] };
      }
      const notes = ['PowerShell does not wait for a Windows-subsystem program unless its output is piped: add | Out-Host to wait.'];
      const values = isLegacy ? args.map(legacyValue) : args;
      if (!args.includes('--%') && !values.includes(null)) {
        return { line: values.map((value) => powerShellLiteral(value ?? '')).join(' '), notes };
      }
      // PowerShell cannot pass --% as a value at all, and 5.1 cannot pass some quoted values: the stop-parsing token passes the raw line.
      notes.push(args.includes('--%')
        ? 'PowerShell reads a --% value as the stop-parsing token however it is quoted, so the whole line follows a --% of its own.'
        : 'Windows PowerShell 5.1 cannot pass one of these values with its own quoting, so the whole line follows --%, as a raw command line.');
      if (!isLegacy && args.some((arg) => arg === '' || /\s/u.test(arg))) {
        return { error: 'PowerShell 7 cannot pass --% as a value, and after a --% of its own it splits at whitespace, so an empty argument or one with a space cannot come with it.', notes: [] };
      }
      const line = isLegacy ? joinWindowsArguments(args) : args.join(' ');
      for (const span of percentSpans(line)) {
        notes.push(`${span} cannot be escaped after --%: it reaches the program as the variable's value whenever that variable is set.`);
      }
      return { line: `--% ${line}`, notes };
    },
    verified: isLegacy ? 'Run from a .ps1 file by powershell.exe -File, for every corpus case.' : 'Run from a .ps1 file by pwsh -File, for every corpus case.'
  };
}

/** A value Windows PowerShell 5.1 turns into exactly the Windows quoting of `arg`, tried in order and checked. */
function legacyValue(arg: string): null | string {
  const inner = escapeWindowsQuoted(arg);
  const candidates = [arg === '' ? '""' : arg, inner, `"${inner}"`];
  return candidates.find((candidate) => {
    const received = splitWindowsArguments(legacyCommandLine([candidate]));
    return received.length === 1 && received[0] === arg;
  }) ?? null;
}

// ---------------------------------------------------------------------------------------------------------------
// bash: Git Bash and WSL.
// ---------------------------------------------------------------------------------------------------------------

/**
 * Reads bash words: `'single'`, `"double"` (where `\` escapes only `$`, `` ` ``, `"`, `\` and a line break), and
 * `\` outside quotes. Expansions, operators and globs are reported, not guessed.
 */
function readBashWords(line: string, notes: string[]): ParsedWords {
  const words: string[] = [];
  let position = 0;
  while (true) {
    while (position < line.length && /[ \t]/u.test(line[position])) {
      position++;
    }
    if (position === line.length) {
      return { words };
    }
    if (line[position] === '#') {
      notes.push('# at the start of a word begins a comment: the rest of the line is ignored.');
      return { words };
    }
    if (line[position] === '~') {
      notes.push('~ at the start of a word expands to the home folder.');
    }
    let value = '';
    while (position < line.length && !/[ \t]/u.test(line[position])) {
      const character = line[position];
      if (character === '\'') {
        const end = line.indexOf('\'', position + 1);
        if (end < 0) {
          return { error: 'The single quote is not closed.', words };
        }
        value += line.slice(position + 1, end);
        position = end + 1;
      } else if (character === '"') {
        position++;
        while (position < line.length && line[position] !== '"') {
          if (line[position] === '\\' && /[$`"\\\n]/u.test(line[position + 1] ?? '')) {
            value += line[position + 1] === '\n' ? '' : line[position + 1];
            position += 2;
          } else if (line[position] === '`' || (line[position] === '$' && /[\w{(@*#?!$-]/u.test(line[position + 1] ?? ''))) {
            return { error: `${line[position]} inside double quotes expands, which this model does not evaluate: use single quotes.`, words };
          } else {
            value += line[position];
            position++;
          }
        }
        if (position === line.length) {
          return { error: 'The double quote is not closed.', words };
        }
        position++;
      } else if (character === '\\') {
        if (position + 1 === line.length) {
          return { error: 'A \\ at the end of the line continues the command on the next line.', words };
        }
        value += line[position + 1];
        position += 2;
      } else if (character === '`' || (character === '$' && /[\w{(@*#?!$'"-]/u.test(line[position + 1] ?? ''))) {
        return { error: `${character} outside single quotes expands, which this model does not evaluate: put the word in single quotes.`, words };
      } else if (/[|&;<>()]/u.test(character)) {
        return { error: `The unquoted ${character} is a bash operator: the program would receive only the words before it.`, words };
      } else {
        if (/[*?[]/u.test(character)) {
          notes.push(`The unquoted ${character} makes the word a glob: it is replaced by matching file names when any exist.`);
        } else if (/[{}]/u.test(character)) {
          notes.push('Unquoted braces can be brace expansion, as in a{b,c}, which yields several words.');
        }
        value += character;
        position++;
      }
    }
    words.push(value);
  }
}

const BASH_BARE = /^[\w./:=+%,@-]+$/u;

function bashLiteral(value: string): string {
  if (BASH_BARE.test(value) && !value.startsWith('~')) {
    return value;
  }
  return `'${value.replaceAll('\'', '\'\\\'\'')}'`;
}

function bashCaller(isGitBash: boolean): Caller {
  return {
    example: isGitBash ? 'MSYS_NO_PATHCONV=1 tool.exe' : 'tool.exe',
    forward(line): ForwardResult {
      const notes: string[] = [];
      const parsed = readBashWords(line, notes);
      if (parsed.error !== undefined) {
        return { error: parsed.error, notes };
      }
      if (isGitBash && parsed.words.some((word) => word.includes('/'))) {
        notes.push('Without MSYS_NO_PATHCONV=1 in front of the program, an argument that looks like a POSIX path, such as /quiet, is rewritten into a Windows path.');
      }
      return { args: parsed.words, notes };
    },
    id: isGitBash ? 'git-bash' : 'wsl',
    name: isGitBash ? 'Git Bash' : 'WSL',
    reverse(args): ReverseResult {
      const error = nulError(args);
      if (error) {
        return { error, notes: [] };
      }
      const notes: string[] = [];
      if (isGitBash) {
        notes.push('Put MSYS_NO_PATHCONV=1 in front of the program, so no argument is rewritten as a POSIX path.');
      } else {
        notes.push('Call the program by its .exe name, with Windows paths: wslpath -w converts a Linux one.');
      }
      return { line: args.map(bashLiteral).join(' '), notes };
    },
    verified: isGitBash ? 'Run from a .sh file by Git Bash with MSYS_NO_PATHCONV=1, for every corpus case.' : 'Run from a .sh file by bash in WSL 2, for every corpus case, with -Wsl.'
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Code: an argument list in Node, Python and C#.
// ---------------------------------------------------------------------------------------------------------------

function codeCaller(id: string, name: string, literal: (value: string) => string, wrap: (items: string) => string): Caller {
  return {
    example: '',
    forward(): ForwardResult {
      return { error: 'An argument list is already the arguments: each string is one argument, quoted by the runtime.', notes: [] };
    },
    id,
    name,
    reverse(args): ReverseResult {
      const error = nulError(args);
      if (error) {
        return { error, notes: [] };
      }
      return { line: wrap(args.map(literal).join(', ')), notes: [] };
    },
    verified: 'Run as a program that starts the recorder with the list, for every corpus case.'
  };
}

function escapeCodeString(value: string, quote: string): string {
  return value.replaceAll(/[\\'"\r\n\t\0]/gu, (character) => {
    switch (character) {
      case '\\':
        return '\\\\';
      case '\r':
        return '\\r';
      case '\n':
        return '\\n';
      case '\t':
        return '\\t';
      case '\0':
        return '\\0';
      default:
        return character === quote ? `\\${character}` : character;
    }
  });
}

// ---------------------------------------------------------------------------------------------------------------
// The callers, in the order the page lists them.
// ---------------------------------------------------------------------------------------------------------------

export const CALLERS: readonly Caller[] = [
  cmdCaller(false),
  cmdCaller(true),
  powerShellCaller(false),
  powerShellCaller(true),
  bashCaller(true),
  bashCaller(false),
  rawCaller({
    example: 'C:\\Tools\\tool.exe',
    shouldExpand: true,
    id: 'shortcut',
    limit: { characters: SHORTCUT_ARGUMENTS_LIMIT, what: 'A shortcut\'s arguments' },
    name: 'Shortcut',
    verified: 'Opened as a double-click opens it, for every corpus case.'
  }),
  rawCaller({
    example: 'C:\\Tools\\tool.exe',
    shouldExpand: true,
    id: 'task-scheduler',
    name: 'Task Scheduler',
    verified: 'Passes the line to the program as the shortcut does; its own example on the shell page is run with -TaskScheduler.'
  }),
  rawCaller({
    example: 'C:\\Tools\\tool.exe',
    shouldExpand: true,
    id: 'run-dialog',
    limit: { characters: RUN_DIALOG_LIMIT, what: 'The Run dialog, program name included,' },
    name: 'Run dialog',
    verified: 'Checked by hand: no script can type into the Run dialog.'
  }),
  rawCaller({
    example: 'tool.exe',
    shouldExpand: false,
    id: 'raw',
    name: 'A raw command line: CreateProcess, ProcessStartInfo.Arguments, Node\'s windowsVerbatimArguments',
    verified: 'Started by CreateProcess with the line, for every corpus case.'
  }),
  codeCaller('node', 'Node (spawnSync, execFile)', (value) => `'${escapeCodeString(value, '\'')}'`, (items) => `[${items}]`),
  codeCaller('python', 'Python (subprocess.run)', (value) => `'${escapeCodeString(value, '\'')}'`, (items) => `[${items}]`),
  codeCaller('csharp', 'C# (ProcessStartInfo.ArgumentList)', (value) => `"${escapeCodeString(value, '"')}"`, (items) => `{ ${items} }`)
];

/** Finds a caller by its id. */
export function getCaller(id: string): Caller {
  const caller = CALLERS.find((candidate) => candidate.id === id);
  if (!caller) {
    throw new Error(`Unknown caller ${id}`);
  }
  return caller;
}

/**
 * The argument values every model is proven on: quotes, backslashes, cmd, PowerShell and bash metacharacters,
 * spaces, empty strings and text outside ASCII.
 */
export const CORPUS: readonly (readonly string[])[] = [
  ['hello world'],
  ['say "hi"'],
  ['C:\\Log files\\'],
  ['C:\\path\\'],
  [''],
  ['a', '', 'b'],
  ['100%'],
  ['%PATH%'],
  ['50% off', '100%'],
  ['%NO_SUCH_VARIABLE%'],
  ['a&b'],
  ['a^b'],
  ['^'],
  ['wow!'],
  ['!PATH!'],
  ['a|b<c>d'],
  ['(paren)'],
  ['"'],
  ['\\"'],
  ['a\\\\"b'],
  ['"a b"'],
  ['a "b c'],
  ['\'single\''],
  ['$HOME', '$(whoami)', '`tick`'],
  ['x;y', 'a,b', '@splat', '{a,b}', '#hash'],
  ['--%'],
  ['--', '--verbose', '-foo.bar', '-x:y', '/quiet'],
  ['  padded  '],
  ['*.txt', '~', '?'],
  ['Привіт світ', '日本語', '😀'],
  ['"%PATH%" & "x"']
];

/** Lines written by hand, as a person would type them, that every forward model is also proven on. */
export const FORWARD_CORPUS: readonly { readonly caller: string; readonly line: string }[] = [
  { caller: 'cmd-prompt', line: '100% "a b" %NO_SUCH_VARIABLE% 5%' },
  { caller: 'cmd-prompt', line: 'a^&b "c&d" say\\"hi\\" ^^' },
  { caller: 'cmd-prompt', line: '"x y" & rem the rest is another command' },
  { caller: 'cmd-batch', line: '100%% %NO_SUCH_VARIABLE% 5% "%%x" a^&b' },
  { caller: 'pwsh', line: '\'it\'\'s\' "a""b" plain `$x' },
  { caller: 'pwsh', line: 'one \'--%\' \'a b\' c' },
  { caller: 'pwsh', line: 'x --% "a b" %NO_SUCH_VARIABLE% \'q\'' },
  { caller: 'powershell', line: '\'a b\' \'\' \'say \\"hi\\"\' \'"a b"\'' },
  { caller: 'powershell', line: 'x --% "a b" %NO_SUCH_VARIABLE%' },
  { caller: 'git-bash', line: '\'a b\' "c\\"d" e\\ f "$" \'\'' },
  { caller: 'raw', line: 'a\\\\"b c" "d e" \\\\server\\share\\ x\\"y' },
  { caller: 'shortcut', line: '"a b" %NO_SUCH_VARIABLE% 100%' }
];

/** Callers whose line cannot keep a `%NAME%` from expanding, so the corpus' set-variable cases are skipped there. */
export function checkExpandsUnescapably(callerId: string): boolean {
  return callerId === 'shortcut' || callerId === 'task-scheduler' || callerId === 'run-dialog';
}
