using UnityEngine;
using UnityEngine.UI;

namespace MoodyBlues.Playground
{
    /// <summary>Self-building HUD line for coins and thrown-target score, placed under the main HUD.</summary>
    public class CoinHud : MonoBehaviour
    {
        [SerializeField] private Vector2 offset = new(16f, -90f);

        private Text _text;

        private void Start()
        {
            Canvas canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) { enabled = false; return; }
            var go = new GameObject("CoinHudText", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(canvas.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = offset;
            rt.sizeDelta = new Vector2(500f, 60f);
            _text = go.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 18;
            _text.color = Color.white;
            _text.raycastTarget = false;
            Collectible.Collected += OnCollected;
            ThrowTarget.Hit += OnHit;
            Refresh();
        }

        private void OnDestroy()
        {
            Collectible.Collected -= OnCollected;
            ThrowTarget.Hit -= OnHit;
        }

        private void OnCollected(Collectible c) => Refresh();
        private void OnHit(ThrowTarget t, Pickable p, int pts) => Refresh();

        private void Refresh()
        {
            if (_text == null) return;
            int total = Collectible.TotalCollected + Collectible.TotalInScene;
            _text.text = $"Coins: {Collectible.TotalCollected}/{total}\nTarget score: {ThrowTarget.TotalHitScore}";
        }
    }
}
