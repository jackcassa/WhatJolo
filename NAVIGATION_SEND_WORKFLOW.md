# Workflow Navigation / Send

1. Avvia loop continuo Send.
2. Verifica progetto selezionato.
3. Verifica ADB.
4. Ricostruisce struttura locale di inferenza dal DB.
5. Ripristina modelli ONNX per:
   - cerca
   - back
   - chat
6. Avvia ADB.
7. Seleziona il primo device collegato.
8. Acquisisce screenshot.
9. Esegue YOLO per cercare "cerca".
10. Se trova "cerca":
    - aspetta 3 secondi
    - fa tap al centro del box
    - aspetta 3 secondi
    - aspetta cambio schermata
11. Se non trova "cerca":
    - salva immagine nel DB come priva_<timestamp>.png
    - interrompe il ciclo corrente
11.1
    altrimenti	    
    - adb manda la sequenza 3204751139
    - pausa
    - aspetta il cambio di schermata
    - esegue YOLO per cercare "chat"
12. Se trova "chat":
    - fa tap sul centro del bounding box trovato
    - aspetta cambio schermata
13. Se non trova "chat":
    - salva immagine nel DB come priva_<timestamp>_chat.png
    - interrompe il ciclo corrente
14. Dopo cambio schermata:
    - acquisisce nuova immagine
    - aggiorna preview
    - esegue YOLO per cercare "back"
15. Se trova "back":
    - aspetta 3 secondi
    - fa tap al centro del box
    - aspetta 3 secondi
    - aspetta nuova immagine
    - aggiorna preview
16. Se non trova "back":
    - salva immagine nel DB come errore_<timestamp>_back.png
    - interrompe il ciclo corrente
17. Scrive log YOLO in navigation_yolo.log.
18. Aspetta 10 secondi.
19. Ripete il ciclo Send.
20. Il pulsante Stop ferma il loop.
