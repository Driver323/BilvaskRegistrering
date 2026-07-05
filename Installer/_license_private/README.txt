Ten folder jest celowo PUSTY w paczkach instalacyjnych.

Prywatny klucz RSA (private.xml) TRZYMAJ TYLKO lokalnie na komputerze build (nie dodawaj do repo i nie wysyłaj klientowi).

Domyślna lokalizacja klucza prywatnego używana przez keygen.ps1:
  %USERPROFILE%\.bilvask_license\private.xml

Możesz też wskazać ścieżkę parametrem:
  .\keygen.ps1 -PrivateKeyPath "D:\sekrety\bilvask\private.xml"
