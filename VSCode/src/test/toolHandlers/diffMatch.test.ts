/**
 * Unit tests for the fuzzy matcher and replacement (toolHandlers/diffMatch.ts) — the TS port of
 * `ToolCore/EditApplier.cs`.
 *
 * Pure functions: this file does NOT import ./setup.ts, because it needs no fs stub (and setup.ts
 * installs a module hook to hand the handler a stubbed 'fs').
 */
import * as chai from 'chai';
// Kept deliberately: each name pins a DISTINCT rule (linesMatch — the matcher, isWholeLineBlock —
// tie-breaker #4, selectEditTarget — the refusal). A future consolidation of the module breaks this
// import (compilation), not the behaviour these tests pin.
import {
    applyEdit,
    isWholeLineBlock,
    linesMatch,
    selectEditTarget,
} from '../../toolHandlers/diffMatch';

const expect = chai.expect;

describe('diffMatch', () => {
    // -----------------------------------------------------------------------
    // selectEditTarget — the tie-breakers of EditApplier.SelectSingle
    // -----------------------------------------------------------------------

    describe('selectEditTarget', () => {
        it('should select the only occurrence', () => {
            const selection = selectEditTarget(['a', 'target', 'b'], ['target'], -1, 5);

            expect(selection.matches).to.deep.equal([1]);
            expect(selection.selected).to.equal(1);
        });

        it('should refuse two whole-line duplicates when no hint singles one out', () => {
            const target = ['same();', 'middle', 'same();'];
            const selection = selectEditTarget(target, ['same();'], -1, 5);

            expect(selection.matches).to.deep.equal([0, 2]);
            expect(selection.selected).to.equal(null);
        });

        it('should narrow duplicates with the hint window', () => {
            const target = ['same();', ...Array.from({ length: 12 }, (_, i) => `// filler ${i}`), 'same();'];

            expect(selectEditTarget(target, ['same();'], -1, 5).selected).to.equal(null);
            expect(selectEditTarget(target, ['same();'], 2, 5).selected).to.equal(0);
            expect(selectEditTarget(target, ['same();'], 14, 5).selected).to.equal(13);
        });

        it('should prefer the exact whole-line block over a fragment match', () => {
            // Tie-breaker #4: 'List<string' is a SUBSTRING of line 0 (fragment) and the exact
            // content of line 2. Without this rule the edit is ambiguous even though one place is
            // an exact match — and with the old whole-line-only matcher the fragment was invisible.
            const target = ['var a = new List<string>();', 'middle', 'List<string'];
            const selection = selectEditTarget(target, ['List<string'], -1, 5);

            expect(selection.matches).to.deep.equal([0, 2]);
            expect(selection.selected).to.equal(2);
        });

        it('should refuse when two candidates are both fragments', () => {
            const target = ['if (a) x();', 'middle', 'if (b) x();'];
            const selection = selectEditTarget(target, ['x();'], -1, 5);

            expect(selection.matches).to.deep.equal([0, 2]);
            expect(selection.selected).to.equal(null);
        });

        it('should apply the hint before the whole-line tie-breaker', () => {
            // Order matters: a hint that singles out a FRAGMENT wins over an exact whole-line match
            // elsewhere — otherwise the model's position hint would be ignored.
            const target = ['if (a) x();', 'b', 'x();'];
            expect(selectEditTarget(target, ['x();'], 1, 0).selected).to.equal(0);
        });

        it('should pick the hinted empty line for an all-empty search, else the first one', () => {
            const target = ['a', '', 'b', '', 'c'];

            expect(selectEditTarget(target, [''], 5, 1).selected).to.equal(3);
            expect(selectEditTarget(target, [''], -1, 5).selected).to.equal(1);
        });

        it('should pick the empty line NEAREST the hint, not the first in the window', () => {
            // Mirror of `EditApplier.FirstEmptyLine`: the window is +/-5 lines, so "first candidate
            // inside it" can be a line the model never pointed at — and the edit still reports
            // success, leaving no signal that it missed.
            //
            // THE FIXTURE MATTERS: both blanks must sit INSIDE the window, or there is a single
            // candidate and "first" equals "nearest" whatever the implementation does. Blanks at
            // 0-based 3 and 6, hint 8 (0-based 7) -> window [3, 6], first = 3, nearest = 6.
            const target = ['a', 'b', 'c', '', 'd', 'e', '', 'f', 'g', 'h', 'i', 'j', 'k'];

            expect(selectEditTarget(target, [''], 8, 5).selected).to.equal(6);
        });

        it('should let a nearer empty line ABOVE the hint win', () => {
            // The mirror case: a rule of "prefer the first candidate at or after the hint" would
            // pass the previous test and fail this one. Both hosts must answer the same.
            const target = ['a', 'b', '', 'c', '', 'd', 'e', 'f', 'g', 'h'];

            expect(selectEditTarget(target, [''], 6, 5).selected).to.equal(4);
        });

        it('should keep the earlier empty line on an exact distance tie', () => {
            // Determinism on a tie, NOT a guard for the nearest-line rule (this fixture answers the
            // same both ways — blanks at 1 and 5, hint 0-based 3, both 2 lines away). It is here so
            // the answer never depends on enumeration order: C# and TS must agree on the same input.
            const target = ['a', '', 'b', 'c', 'd', '', 'f'];

            expect(selectEditTarget(target, [''], 4, 5).selected).to.equal(1);
        });

        it('should report no selection when nothing matches', () => {
            const selection = selectEditTarget(['a', 'b'], ['zzz'], -1, 5);

            expect(selection.matches).to.deep.equal([]);
            expect(selection.selected).to.equal(null);
        });
    });

    // -----------------------------------------------------------------------
    // linesMatch — the ported EditApplier.LinesMatch
    // -----------------------------------------------------------------------

    describe('linesMatch', () => {
        it('should match a single-line search as a substring', () => {
            expect(linesMatch(['var x = 1;'], 0, ['x = 1'])).to.be.true;
            expect(linesMatch(['var x = 1;'], 0, ['y = 1'])).to.be.false;
        });

        it('should match a first boundary line truncated at the START', () => {
            expect(linesMatch(['    var someLong = Compute();', '}'], 0, ['someLong = Compute();', '}'])).to.be.true;
            // The first line may lose its head, but the LAST line may not lose its head:
            expect(linesMatch(['head', 'tail'], 0, ['head', 'il'])).to.be.false;
        });

        it('should match a last boundary line truncated at the END', () => {
            expect(linesMatch(['head', '    DoSomething(value);'], 0, ['head', 'DoSomething(val'])).to.be.true;
            // The first line may not be truncated at the end (that would be a different position):
            expect(linesMatch(['he', 'tail'], 0, ['head', 'tail'])).to.be.false;
        });

        it('should require middle lines to match exactly', () => {
            expect(linesMatch(['a', 'b FOO c', 'd'], 0, ['a', 'foo', 'd'])).to.be.false;
            expect(linesMatch(['a', 'FOO', 'd'], 0, ['a', 'foo', 'd'])).to.be.true;
        });

        it('should match an empty search line only against an empty target line', () => {
            expect(linesMatch(['a', '   '], 0, ['a', ''])).to.be.true;
            expect(linesMatch(['a', 'b'], 0, ['a', ''])).to.be.false;
        });

        it('should compare case-insensitively', () => {
            expect(linesMatch(['ABC'], 0, ['abc'])).to.be.true;
        });

        it('should distinguish a whole-line block from a fragment hit', () => {
            expect(isWholeLineBlock(['  target  '], 0, ['target'])).to.be.true;
            expect(isWholeLineBlock(['xx target'], 0, ['target'])).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // applyEdit — the ported EditApplier.ApplyAt
    // -----------------------------------------------------------------------

    describe('applyEdit replacement', () => {
        it('should replace a whole line VERBATIM with its own indentation', () => {
            const target = ['class A', '{', '    var x = 1;', '}'];

            const outcome = applyEdit(target, ['  var x = 1;  '], ['        if (x)'], -1, 0);

            expect(outcome.outcome).to.equal('applied');
            expect(outcome.startLine).to.equal(2);
            expect(target).to.deep.equal(['class A', '{', '        if (x)', '}']);
        });

        it('should replace only the matched fragment of a single-line search', () => {
            // Matching without this half of the port would delete the whole line.
            const target = ['var fileContent = new List<string>'];

            applyEdit(target, ['List<string'], ['List<string?'], -1, 0);

            expect(target).to.deep.equal(['var fileContent = new List<string?>']);
        });

        it('should keep the trailing part on the last emitted line with extra replacement lines', () => {
            const target = ['var f = new List<string>'];

            applyEdit(target, ['List<string'], ['List<int>', '// note'], -1, 0);

            expect(target).to.deep.equal(['var f = new List<int>', '// note>']);
        });

        it('should keep the suffix of a truncated first boundary line', () => {
            const target = ['public void Method()', '{', '    var v = Compute();', '}'];

            applyEdit(target, ['v = Compute();', '}'], ['v = Compute(1);', '}'], -1, 0);

            expect(target).to.deep.equal(['public void Method()', '{', '    var v = Compute(1);', '}']);
        });

        it('should delete the extra search lines when the replacement is shorter', () => {
            const target = ['a', 'b', 'c', 'd'];

            applyEdit(target, ['b', 'c', 'd'], ['B'], -1, 0);

            expect(target).to.deep.equal(['a', 'B']);
        });

        it('should attach the suffix to the preceding line when the whole block is deleted', () => {
            const target = ['class A', '{', '    var x = 1;', '    DoSomething(value);', '}'];

            applyEdit(target, ['    var x = 1;', '    DoSomething(val'], [], -1, 0);

            expect(target).to.deep.equal(['class A', '{ue);', '}']);
        });

        it('should absorb the indentation of a last boundary line without a replacement', () => {
            const target = ['class A', '{', '    var x = 1;', '    DoSomething(value);', '}'];

            applyEdit(target, ['    var x = 1;', '    DoSomething(val'], ['    var x = 2;'], -1, 0);

            expect(target).to.deep.equal(['class A', '{', '    var x = 2;ue);', '}']);
        });

        it('should remove every line of an all-empty search block', () => {
            // The branch removes search.length lines, not one — mirroring the C# rule.
            const target = ['a', '', '', 'b'];

            applyEdit(target, ['', ''], ['X', 'Y'], -1, 0);

            expect(target).to.deep.equal(['a', 'X', 'Y', 'b']);
        });

        it('should refuse an ambiguous edit and leave the target untouched', () => {
            const target = ['if (a) x();', 'middle', 'if (b) x();'];

            const outcome = applyEdit(target, ['x();'], ['y();'], -1, 5);

            expect(outcome.outcome).to.equal('ambiguous');
            expect(outcome.matchLines).to.deep.equal([0, 2]);
            expect(outcome.startLine).to.equal(-1);
            expect(target).to.deep.equal(['if (a) x();', 'middle', 'if (b) x();']);
        });

        it('should replace every occurrence bottom-to-top with replaceAll', () => {
            const target = ['x();', 'keep', 'x();', 'x();'];

            const outcome = applyEdit(target, ['x();'], ['y();'], -1, 5, true);

            expect(outcome.outcome).to.equal('applied');
            expect(outcome.appliedCount).to.equal(3);
            expect(target).to.deep.equal(['y();', 'keep', 'y();', 'y();']);
        });

        it('should skip overlapping candidates under replaceAll', () => {
            // A multi-line search in a repeated block yields candidates that are no longer distinct
            // occurrences once the later one is rewritten. Every other fixture used non-overlapping
            // matches, so deleting the skip produced identical output.
            const target = ['a', 'a', 'a'];

            const outcome = applyEdit(target, ['a', 'a'], ['X'], -1, 0, true);

            expect(outcome.outcome).to.equal('applied');
            expect(outcome.appliedCount).to.equal(1);
            expect(target).to.deep.equal(['a', 'X']);
        });

        it('should NOT let replaceAll take an all-empty search', () => {
            // `oldStr: "\n"` is a natural spelling of "a blank line" and splits to ONE empty search
            // line, which matches EVERY blank line. `replaceAll` must not decide the scope of an
            // empty pattern: the C# twin settles it before looking at the flag, and the two hosts
            // must not rewrite different numbers of lines for the same call.
            const target = ['', 'a', '', 'b', ''];

            // tol 0 so the window holds line 2 alone; with the default tolerance the hint covers the
            // whole fixture and "first blank in the window" IS line 0 — which is the documented rule,
            // not a bug, and would make this test say nothing about the flag.
            const outcome = applyEdit(target, [''], ['// TODO'], 3, 0, true);

            expect(outcome.outcome).to.equal('applied');
            expect(outcome.appliedCount).to.equal(1);
            expect(target).to.deep.equal(['', 'a', '// TODO', 'b', '']);
        });

        it('should use the hinted blank line when the window holds several', () => {
            // An empty search is never ambiguous, so "the window holds more than one" must not send
            // the edit to the FIRST blank line of the file. TWO blanks have to sit inside the window:
            // with one, both implementations answer the same and the test proves nothing.
            const target = ['a', '', 'b', 'c', 'd', 'e', 'f', '', 'h', 'i', 'j', ''];

            const outcome = applyEdit(target, [''], ['X'], 10, 5);

            expect(outcome.startLine).to.equal(7);
            expect(target).to.deep.equal(['a', '', 'b', 'c', 'd', 'e', 'f', 'X', 'h', 'i', 'j', '']);
        });

        it('should report notFound when the search block is longer than the file', () => {
            const outcome = applyEdit(['a'], ['a', 'b'], ['x'], -1, 0);

            expect(outcome.outcome).to.equal('notFound');
            expect(outcome.matchLines).to.deep.equal([]);
        });

        it('should keep the tail when a single-line search starts the line', () => {
            // Reversed decision: dropping the tail here made the result depend on indentation —
            // `x();` in `x(); // keep` at column 0 lost the comment while the indented form kept
            // it. Only the matched part is replaced, wherever it sits in the line.
            const target = ['DoA(); // keep me?'];

            applyEdit(target, ['DoA();'], ['DoB();'], -1, 0);

            expect(target).to.deep.equal(['DoB(); // keep me?']);
        });

        it('should keep the tail of a single-line fragment that does not start the line', () => {
            const target = ['if (ready) x(); // keep'];

            applyEdit(target, ['x();'], ['y();'], -1, 0);

            expect(target).to.deep.equal(['if (ready) y(); // keep']);
        });
    });
});
