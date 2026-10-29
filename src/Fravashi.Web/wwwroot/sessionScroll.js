// Stick-to-bottom scrolling for the agent transcript (backlog 17).
//
// Why an observer rather than a Blazor call per update: the transcript grows once per streamed token,
// and on Blazor Server every interop hop is a SignalR round trip. A MutationObserver watching the
// scroller does the pinning entirely client side, so C# calls in exactly ONCE per element (attach)
// instead of once per token.
//
// The contract with the component: while the reader is at (or near) the bottom the view follows the
// newest content; the moment they scroll up we stop pinning and tell C# so it can offer a
// "jump to latest" button. We never scroll on the reader's behalf while they are away, which is the
// "do not yank them back down" half of the behaviour.
//
// All state lives on the element itself, so a re-render or a reconnected circuit cannot lose track of
// where the reader is.
window.fravashiSessionScroll = (function () {
    'use strict';

    // Close enough to the bottom that the reader is watching the stream rather than reading history.
    const NEAR_BOTTOM_PX = 40;
    // How long after the last wheel, touch or key a scroll still counts as the reader's own. Long enough
    // for smooth scrolling and wheel inertia to finish, short enough that a layout change after it is not
    // mistaken for them.
    const INPUT_WINDOW_MS = 1000;
    const STATE = '__fravashiSessionScroll';

    function atBottom(el) {
        return (el.scrollHeight - el.scrollTop - el.clientHeight) <= NEAR_BOTTOM_PX;
    }

    // Tells the component whether the reader has moved away from the newest content, but only when the
    // answer changed: a scroll handler must not chatter over the circuit.
    function report(state) {
        const away = !state.stick;
        if (away === state.reported || !state.ref) {
            return;
        }

        state.reported = away;
        try {
            const pending = state.ref.invokeMethodAsync('AwayFromBottomChanged', away);
            if (pending && pending.catch) {
                pending.catch(function () { /* disposed component or dead circuit */ });
            }
        } catch (e) {
            // A scroll handler must never throw.
        }
    }

    // Coalesced to one layout write per frame: a burst of streamed tokens is one pin, not twenty.
    //
    // This write is also why the transcript can leave the browser's scroll anchoring on (studio.css):
    // while stuck, the position we write here lands after any anchoring adjustment and wins, so the two
    // never disagree. While the reader is away we do not write at all, and anchoring is then the only
    // thing holding their place when a tool card above them grows its output.
    function pin(state) {
        if (!state.stick) {
            return;
        }

        // A hidden page gets no animation frames. Measured in the installed window on 2026-09-21 (backlog 17):
        // with the studio behind other windows, which is how a turn is normally left to run, the frame asked
        // for here never came, `scheduled` stayed true for the whole turn, and the transcript sat at the top
        // while 940px of answer streamed in under it. There is nothing to coalesce into when nothing is being
        // painted, so the position is written directly, and a frame that was already waiting is harmless: it
        // runs when the window comes back and writes the same value.
        if (document.hidden) {
            state.el.scrollTop = state.el.scrollHeight;
            return;
        }

        if (state.scheduled) {
            return;
        }

        state.scheduled = true;
        requestAnimationFrame(function () {
            state.scheduled = false;
            if (state.stick) {
                state.el.scrollTop = state.el.scrollHeight;
            }
        });
    }

    return {
        attach: function (el, dotnetRef) {
            if (!el) {
                return;
            }

            const existing = el[STATE];
            if (existing) {
                // A reconnected circuit renders a fresh component into the same DOM. Keep the reader's
                // position and the live listeners, just hand the new component the current answer.
                existing.ref = dotnetRef;
                existing.reported = null;
                report(existing);
                return;
            }

            const state = {
                el: el,
                ref: dotnetRef,
                stick: true,
                reported: false,
                scheduled: false,
                observer: null,
                resize: null,
                held: false,
                inputUntil: 0
            };
            el[STATE] = state;

            // A scroll event says the position changed, not WHO changed it, and only the reader may
            // unstick the reader. Two ways the position moves with nobody touching anything were measured
            // in the studio window on a live turn (backlog 17), and both used to read as "they walked away",
            // stop the pin, and leave the rest of the turn streaming out of sight behind a Jump to latest
            // nobody had asked for:
            //   - the approval prompt and the stats strip are SIBLINGS of the transcript, so when one
            //     appears the viewport shrinks (a 168px approval left the scroller 168px from the bottom,
            //     past the 40px tolerance);
            //   - the 20px streaming draft is replaced by the final message, so the content shrinks for a
            //     moment, the browser clamps scrollTop, and that clamp's scroll event is only evaluated
            //     after a 1292px message has landed under it.
            // Special-casing each one is how the second was missed. So the rule is about intent: a scroll
            // counts as the reader's only while they are wheeling, touching, pressing a key, or holding a
            // pointer down on the scroller (which is what dragging its scrollbar is).
            function readerInput() {
                state.inputUntil = performance.now() + INPUT_WINDOW_MS;
            }

            ['wheel', 'touchstart', 'touchmove', 'keydown'].forEach(function (type) {
                el.addEventListener(type, readerInput, { passive: true });
            });
            el.addEventListener('pointerdown', function () { state.held = true; readerInput(); }, { passive: true });
            state.release = function () { if (state.held) { state.held = false; readerInput(); } };
            window.addEventListener('pointerup', state.release, { passive: true });
            window.addEventListener('pointercancel', state.release, { passive: true });

            function readerIsScrolling() {
                return state.held || performance.now() < state.inputUntil;
            }

            // Put a stuck reader back. Written directly rather than through pin(): waiting a frame is what
            // lets the next scroll event see the gap.
            function hold() {
                if (state.stick && !atBottom(el)) {
                    el.scrollTop = el.scrollHeight;
                }
            }

            el.addEventListener('scroll', function () {
                if (state.stick && !readerIsScrolling()) {
                    hold();
                    return;
                }

                state.stick = atBottom(el);
                report(state);
            }, { passive: true });

            // A viewport that changes size need not fire a scroll event at all, and the observer below
            // cannot see it either: the sibling that took the space is outside the scroller's subtree.
            if (typeof ResizeObserver === 'function') {
                state.resize = new ResizeObserver(hold);
                state.resize.observe(el);
            }

            // Work item #351: a reference in the conversation (#343, @159, !12) is a real link to its page, and a
            // plain click on one opens its preview beside the conversation instead. Delegated here because most
            // of them are inside rendered markdown, where Blazor cannot put a handler; and the default is
            // prevented and the event stopped so neither the studio's navigation nor a tool row's own toggle
            // (a reference can sit inside the row's button) also acts on it. A modified click keeps the link's
            // own behaviour, and so does every click once the component is gone.
            state.click = function (event) {
                if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
                    return;
                }

                const link = event.target && event.target.closest ? event.target.closest('a.wiki-ref') : null;
                if (!link || !el.contains(link) || !state.ref) {
                    return;
                }

                event.preventDefault();
                event.stopPropagation();
                try {
                    const pending = state.ref.invokeMethodAsync('OpenReference', (link.textContent || '').trim());
                    if (pending && pending.catch) {
                        pending.catch(function () { /* disposed component or dead circuit */ });
                    }
                } catch (e) {
                    // A click handler must never throw.
                }
            };
            el.addEventListener('click', state.click);

            // characterData matters: a tool card flips its status text in place without adding a node,
            // and the streamed reply grows an existing text node.
            state.observer = new MutationObserver(function () { pin(state); });
            state.observer.observe(el, { childList: true, subtree: true, characterData: true });

            el.scrollTop = el.scrollHeight;
        },

        // Instant, not smooth: a smooth scroll fires intermediate scroll events that would re-show the
        // jump button half way down.
        toBottom: function (el) {
            if (!el) {
                return;
            }

            el.scrollTop = el.scrollHeight;
            const state = el[STATE];
            if (state) {
                state.stick = true;
                report(state);
            }
        },

        detach: function (el) {
            const state = el && el[STATE];
            if (!state) {
                return;
            }

            if (state.observer) {
                state.observer.disconnect();
            }

            if (state.resize) {
                state.resize.disconnect();
            }

            if (state.click) {
                el.removeEventListener('click', state.click);
            }

            if (state.release) {
                window.removeEventListener('pointerup', state.release);
                window.removeEventListener('pointercancel', state.release);
            }

            state.ref = null;
            el[STATE] = null;
        }
    };
})();
