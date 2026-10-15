using System.Collections.Generic;
using CWShim;
using UnityEngine;

namespace CWIOS
{
    /// <summary>
    /// Fingers -> virtual input. While the game shows a cursor (menus, escape menu, terminals) touch is the mouse:
    /// one finger moves the pointer and holds the left button (tap = click, drag = drag), two fingers scroll.
    /// The FPS counter corner opens the log viewer. (Gameplay controls - stick, look, buttons - come with the HUD.)
    /// </summary>
    public class TouchLayer : MonoBehaviour
    {
        readonly HashSet<int> m_Consumed = new HashSet<int>();
        int m_Pointer = -1;
        bool m_Scrolling;
        Vector2 m_LastScrollPos;

        public static bool CursorFree => VCursor.LockState != CursorLockMode.Locked;

        void OnEnable() => VirtualInput.Tick += Tick;
        void OnDisable() => VirtualInput.Tick -= Tick;

        void Tick()
        {
            int count = Input.touchCount;
            int free = 0;
            Vector2 sum = Vector2.zero;
            Touch primary = default;
            bool havePrimary = false;
            for (int i = 0; i < count; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && DebugOverlay.TryBeginTouch(t.position)) m_Consumed.Add(t.fingerId);
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (m_Consumed.Contains(t.fingerId))
                {
                    if (ended) m_Consumed.Remove(t.fingerId);
                    continue;
                }
                if (ended)
                {
                    if (t.fingerId == m_Pointer) m_Pointer = -1;
                    continue;
                }
                free++;
                sum += t.position;
                if (m_Pointer < 0 && !m_Scrolling) m_Pointer = t.fingerId;
                if (t.fingerId == m_Pointer) { primary = t; havePrimary = true; }
            }
            if (DebugOverlay.ShowingLog) { m_Pointer = -1; return; }
            if (!CursorFree) { m_Pointer = -1; m_Scrolling = false; return; }

            if (free >= 2)
            {
                // two fingers: scroll by their average vertical movement; the pointer press is dropped
                Vector2 avg = sum / free;
                if (m_Scrolling) VirtualInput.Scroll += (avg.y - m_LastScrollPos.y) / (Screen.height * 0.03f);
                m_LastScrollPos = avg;
                m_Scrolling = true;
                m_Pointer = -1;
                return;
            }
            if (free == 0) m_Scrolling = false;
            if (m_Scrolling || !havePrimary) return;
            VirtualInput.PointerPos = primary.position;
            VirtualInput.LeftButton = true;
        }
    }
}
