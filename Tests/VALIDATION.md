# Doğrulama — 11 Ekim 2026 (arayüz ve davet linki güncellemesi)

- Unity 6000.3.10f1: derleme 0 hata, 0 uyarı. WebGL ve Windows development build'leri 0 hata, 0 uyarı ile alındı (`Builds/WebGL-Dev`, `Builds/SelfHosted`).
- Ekranlar 1080x2400 dikey çözünürlükte (WebGL şablonunun oranı) Play Mode ekran görüntüleriyle kontrol edildi: ana menü, lobi, oyun, bitiş.
- `GameplaySmoke.cs` editörde geçti: ana menü seçenekleri, oda kodu ve davet linki ayrıştırma, adres doğrulama, süre/eşleşme/son saniye kazanımı, oyun ve bitiş ekranından ana menüye dönüş, masaüstünde oda kurma ve lobi, adres kopyalama, rakip ayrılınca lobi, iptal, ayrı sunucu.
- Tarayıcı (yerel sunucudaki WebGL build, iki ayrı headless Chrome süreci, gerçek PeerJS aracısı):
  - A **CREATE ROOM** → oda kodu ve `http://localhost:8099/?room=KOD` davet linki lobi ekranında göründü.
  - B davet linkini açtı → kod kendiliğinden doldu, davet mesajı göründü. **JOIN** → bağlandı, tur başladı.
  - A iki kart açtı → B'de aynı kartlar açık göründü; sıra B'ye geçti, B'nin açtığı kartlar A'da göründü; sıra/skor yazıları iki tarafta doğru.
  - B sekmesini kapattı → A lobiye döndü ("Your friend left the game."), oda ve link açık kaldı.
  - Test, commit edilen son kodun WebGL build'i ile tekrarlandı ve geçti.
- Ayrı sunucu (Windows build, `-listen 127.0.0.1`) + editör istemcisi + ikinci istemci (`NetworkSmoke.cs`):
  - Dokuz çift sunucuda eşleşti; bitiş ekranı açıldığı anda "You win! / You 9 - 0 Opponent" gösterdi; tek rövanş oyu yeni tur başlatmadı.
  - İkinci istemci tur ortasında kapatıldı → editör lobide "WAITING FOR A PLAYER", sunucu adresi ve "Your opponent left the game." gösterdi.
- Bu turda düzeltilen hatalar:
  - Bitiş ekranı ayrı bir RPC ile açılıyordu; RPC skor değişkenlerinden önce ulaşırsa sonuç boş/eski görünüyordu. Artık tur bitti değişkeni false'tan true'ya geçerken açılıyor.
  - (Geliştirme sırasında yakalandı) Rakip ayrılınca gelen sıfırlama, lobinin üstüne boş bitiş ekranı açıyordu.
  - Bağlantı hatası mesajı, tek oyunculu oyundan menüye dönünce tekrar görünüyordu.
  - Tarayıcıda oda kurulamıyordu (yalnızca masaüstü host olabiliyordu).

Test edilmedi: gerçek telefonda paylaşım menüsü (Web Share), tarayıcıda pano izni (headless testte kopyalama denenmedi), farklı ağlardaki iki cihaz arasında WebRTC (NAT/TURN), HTTPS üzerinde barındırma, Linux build.

`NetworkSmoke.cs`, ayrı sunucuya oyuncu 0 olarak bağlanmış Play Mode istemcisinde, ikinci istemci bağlanıp tur başladıktan sonra KitWright `execute_code` ile çalıştırılır. Sonuç `UnityEditor.SessionState.GetString("CardMatching.NetworkSmoke", "")` ile okunur. Testler çalışırken başka `execute_code` çağrısı yapmayın.
