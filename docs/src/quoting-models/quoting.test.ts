/**
 * @file
 *
 * Unit tests of the quoting models, run by `node --test`. The real callers are checked by
 * `scripts/test-quoting-models.ps1`; these pin the models' own rules and their agreement with each other.
 */

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { CALLERS, CORPUS, FORWARD_CORPUS, getCaller, joinWindowsArguments, quoteWindowsArgument, splitWindowsArguments } from './quoting.ts';

const CODE_CALLERS = new Set(['csharp', 'node', 'python']);

describe('splitWindowsArguments', () => {
  it('reads quotes and backslashes by the C runtime rules', () => {
    assert.deepEqual(splitWindowsArguments('a "b c" d\\"e f\\\\"g h" i\\\\\\"j k\\'), ['a', 'b c', 'd"e', 'f\\g h', 'i\\"j', 'k\\']);
  });

  it('reads "" inside quotes as a literal quote', () => {
    assert.deepEqual(splitWindowsArguments('"a""b" c'), ['a"b', 'c']);
  });

  it('runs an unclosed quote to the end', () => {
    assert.deepEqual(splitWindowsArguments('x "a b'), ['x', 'a b']);
  });

  it('gives an empty argument for ""', () => {
    assert.deepEqual(splitWindowsArguments(' "" \t ""'), ['', '']);
  });
});

describe('quoteWindowsArgument', () => {
  it('leaves a plain argument bare', () => {
    assert.equal(quoteWindowsArgument('--verbose'), '--verbose');
  });

  it('doubles backslashes before a quote and at the end', () => {
    assert.equal(quoteWindowsArgument('C:\\Log files\\'), '"C:\\Log files\\\\"');
    assert.equal(quoteWindowsArgument('say \\"hi'), '"say \\\\\\"hi"');
  });

  it('round-trips the corpus through the split', () => {
    for (const args of CORPUS) {
      assert.deepEqual(splitWindowsArguments(joinWindowsArguments(args)), args);
    }
  });
});

describe('every caller', () => {
  for (const caller of CALLERS) {
    it(`${caller.id}: the forward model reads back what the reverse model writes, for every corpus case`, () => {
      for (const args of CORPUS) {
        const reversed = caller.reverse(args);
        if (reversed.line === undefined) {
          assert.ok(reversed.error, `${caller.id} gives neither a line nor a reason for ${JSON.stringify(args)}`);
          continue;
        }
        if (CODE_CALLERS.has(caller.id)) {
          continue;
        }
        assert.deepEqual(caller.forward(reversed.line).args, args, `${caller.id}: ${reversed.line}`);
      }
    });
  }

  it('has a hand-typed line for a caller that exists', () => {
    for (const { caller } of FORWARD_CORPUS) {
      assert.ok(getCaller(caller));
    }
  });
});

describe('cmd', () => {
  const prompt = getCaller('cmd-prompt');
  const batch = getCaller('cmd-batch');

  it('escapes operators outside quotes and leaves them inside', () => {
    assert.equal(prompt.reverse(['a&b', 'c d&e']).line, 'a^&b "c d&e"');
  });

  it('breaks a %NAME% at the prompt and doubles % in a batch file', () => {
    assert.equal(prompt.reverse(['%PATH%']).line, '%^PATH%');
    assert.equal(batch.reverse(['%PATH%', 'a b%']).line, '%%PATH%% "a b%%"');
  });

  it('escapes every quote when a prompt % would sit inside cmd\'s quotes', () => {
    assert.equal(prompt.reverse(['50% off', '100%']).line, '^"50%^ off^" 100%');
  });

  it('expands a set variable and stops at an operator', () => {
    const result = prompt.forward('%HOME% "x y" & echo', new Map([['home', 'C:\\Users\\me']]));
    assert.deepEqual(result.args, ['C:\\Users\\me', 'x y']);
    assert.ok(result.notes.some((note) => note.includes('cmd operator')));
  });

  it('drops a lone % and empties an unset variable in a batch file', () => {
    assert.deepEqual(batch.forward('100% x').args, ['100', 'x']);
    assert.deepEqual(batch.forward('a %UNSET% x').args, ['a', 'x']);
    // A lone % pairs with the next one: "% %" names a variable called " ", unset, and the last % is alone.
    assert.deepEqual(batch.forward('100% %UNSET% x').args, ['100UNSET', 'x']);
  });

  it('refuses a line break', () => {
    assert.ok(prompt.reverse(['a\nb']).error);
  });
});

describe('PowerShell', () => {
  const pwsh = getCaller('pwsh');
  const powershell = getCaller('powershell');

  it('single-quotes what is not plain', () => {
    assert.equal(pwsh.reverse(['plain', 'it\'s', '', 'a,b']).line, 'plain \'it\'\'s\' \'\' \'a,b\'');
  });

  it('passes the whole list after a --% of its own when one value is --%', () => {
    assert.equal(pwsh.reverse(['x', '--%']).line, '--% x --%');
    assert.ok(pwsh.reverse(['--%', 'a b']).error);
    assert.equal(powershell.reverse(['--%', 'a b']).line, '--% --% "a b"');
  });

  it('writes \\" and the doubled trailing backslash for Windows PowerShell 5.1', () => {
    assert.equal(powershell.reverse(['say "hi"', 'C:\\Log files\\']).line, '\'say \\"hi\\"\' \'C:\\Log files\\\\\'');
  });

  it('models Windows PowerShell 5.1 dropping an empty value', () => {
    assert.deepEqual(powershell.forward('a \'\' b').args, ['a', 'b']);
    assert.deepEqual(powershell.reverse(['']).line, '\'""\'');
  });

  it('reports what it does not evaluate', () => {
    assert.ok(pwsh.forward('$HOME').error);
    assert.ok(pwsh.forward('a;b').error);
  });
});

describe('bash', () => {
  const gitBash = getCaller('git-bash');

  it('single-quotes what is not plain', () => {
    assert.equal(gitBash.reverse(['a b', 'it\'s', '/quiet']).line, '\'a b\' \'it\'\\\'\'s\' /quiet');
  });

  it('reads double quotes, backslashes and a lone $', () => {
    assert.deepEqual(gitBash.forward('"a\\"b" c\\ d "$"').args, ['a"b', 'c d', '$']);
  });

  it('reports an expansion', () => {
    assert.ok(gitBash.forward('$HOME').error);
  });
});

describe('raw command lines', () => {
  it('notes a %NAME% a shortcut cannot escape', () => {
    const result = getCaller('shortcut').reverse(['%PATH%']);
    assert.equal(result.line, '%PATH%');
    assert.ok(result.notes.some((note) => note.includes('cannot be escaped')));
  });

  it('notes the Run dialog limit', () => {
    assert.ok(getCaller('run-dialog').reverse(['x'.repeat(300)]).notes.some((note) => note.includes('259')));
  });
});

describe('code', () => {
  it('writes string literals', () => {
    assert.equal(getCaller('node').reverse(['C:\\x\\', 'it\'s']).line, '[\'C:\\\\x\\\\\', \'it\\\'s\']');
    assert.equal(getCaller('csharp').reverse(['say "hi"']).line, '{ "say \\"hi\\"" }');
  });
});
