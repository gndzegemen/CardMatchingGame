# Doğrulama — 11 Ekim 2026

- Unity 6000.3.10f1: kod derlemesi başarılı.
- Windows Development build: başarılı; 0 hata, 0 uyarı. Çıktı: `Builds/SelfHosted/CardMatchingGame.exe`.
- `GameplaySmoke.cs`: süre, son saniye kazanımı, eşleşme/yanlış eşleşme, dağıtımda çıkış, reset, adres ayrıştırma, host ve ayrı sunucu kontrolleri geçti.
- Ayrı Windows sunucu süreci + Unity editörü istemcisi + ikinci Windows istemcisi, `127.0.0.1:17778` üzerinden bağlandı.
- 18 kartlık tahta alındı; dokuz çift sunucuda eşleşti. Sunucu günlüğünde ve istemci sonucunda skor 9–0 doğrulandı.
- Bitiş RPC mesajı puan değişkenlerinden önce gelebiliyor; ağ testi son puanın gelmesini bekleyecek şekilde düzeltildi. İstemcide nihai puan ve kazanma metni doğrulandı.
- Tek yeniden oynama oyu turu başlatmadı; rakip onayı beklendi.
- Üçüncü istemci bağlanmayı denediğinde kurulu TCP istemci bağlantıları iki olarak kaldı.
- Rakip süreç kapandığında kalan oyuncu ana menüye döndü; tahta temizlendi, sunucu bağlantısı açık kaldı.
- Son çalışma zamanı konsolunda hata görülmedi. Test süreçleri kapatıldı.

İnternet/NAT, WSS ters proxy, WebGL ve Linux build bu bilgisayarda test edilmedi.

`NetworkSmoke.cs`, ayrı sunucuya oyuncu 0 olarak bağlanmış Play Mode istemcisinde, yeni tur başladıktan sonra KitWright `execute_code` ile çalıştırılır. İkinci istemci bağlı olmalıdır. Sonuç `UnityEditor.SessionState.GetString("CardMatching.NetworkSmoke", "")` ile okunur.
