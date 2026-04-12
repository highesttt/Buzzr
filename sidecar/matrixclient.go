package main

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/rs/zerolog/log"
	_ "github.com/mattn/go-sqlite3"

	"maunium.net/go/mautrix"
	"maunium.net/go/mautrix/crypto"
	"maunium.net/go/mautrix/crypto/backup"
	"maunium.net/go/mautrix/crypto/cryptohelper"
	"maunium.net/go/mautrix/crypto/ssss"
	"maunium.net/go/mautrix/crypto/verificationhelper"
	"maunium.net/go/mautrix/event"
	"maunium.net/go/mautrix/id"
)

const (
	BeeperHomeserver = "https://matrix.beeper.com"
	BeeperAPIBase    = "https://api.beeper.com"
	DeviceDisplayName = "BeeperWinUI Sidecar"
)

type VerificationState struct {
	mu       sync.RWMutex
	Active   bool   `json:"active"`
	TxnID    string `json:"txnID,omitempty"`
	Status   string `json:"status"`
	Emojis   []rune `json:"-"`
	Decimals []int  `json:"decimals,omitempty"`
	Error    string `json:"error,omitempty"`
}

type SASEmojiDescription struct {
	Emoji       string `json:"emoji"`
	Description string `json:"description"`
}

var sasEmojiTable = map[rune]SASEmojiDescription{
	0:  {Emoji: "\U0001F436", Description: "Dog"},
	1:  {Emoji: "\U0001F431", Description: "Cat"},
	2:  {Emoji: "\U0001F981", Description: "Lion"},
	3:  {Emoji: "\U0001F40E", Description: "Horse"},
	4:  {Emoji: "\U0001F984", Description: "Unicorn"},
	5:  {Emoji: "\U0001F437", Description: "Pig"},
	6:  {Emoji: "\U0001F418", Description: "Elephant"},
	7:  {Emoji: "\U0001F430", Description: "Rabbit"},
	8:  {Emoji: "\U0001F43C", Description: "Panda"},
	9:  {Emoji: "\U0001F413", Description: "Rooster"},
	10: {Emoji: "\U0001F427", Description: "Penguin"},
	11: {Emoji: "\U0001F422", Description: "Turtle"},
	12: {Emoji: "\U0001F41F", Description: "Fish"},
	13: {Emoji: "\U0001F419", Description: "Octopus"},
	14: {Emoji: "\U0001F98B", Description: "Butterfly"},
	15: {Emoji: "\U0001F337", Description: "Flower"},
	16: {Emoji: "\U0001F333", Description: "Tree"},
	17: {Emoji: "\U0001F335", Description: "Cactus"},
	18: {Emoji: "\U0001F344", Description: "Mushroom"},
	19: {Emoji: "\U0001F30F", Description: "Globe"},
	20: {Emoji: "\U0001F319", Description: "Moon"},
	21: {Emoji: "\u2601\uFE0F", Description: "Cloud"},
	22: {Emoji: "\U0001F525", Description: "Fire"},
	23: {Emoji: "\U0001F34C", Description: "Banana"},
	24: {Emoji: "\U0001F34E", Description: "Apple"},
	25: {Emoji: "\U0001F353", Description: "Strawberry"},
	26: {Emoji: "\U0001F33D", Description: "Corn"},
	27: {Emoji: "\U0001F355", Description: "Pizza"},
	28: {Emoji: "\U0001F382", Description: "Cake"},
	29: {Emoji: "\u2764\uFE0F", Description: "Heart"},
	30: {Emoji: "\U0001F600", Description: "Smiley"},
	31: {Emoji: "\U0001F916", Description: "Robot"},
	32: {Emoji: "\U0001F3A9", Description: "Hat"},
	33: {Emoji: "\U0001F453", Description: "Glasses"},
	34: {Emoji: "\U0001F527", Description: "Spanner"},
	35: {Emoji: "\U0001F385", Description: "Santa"},
	36: {Emoji: "\U0001F44D", Description: "Thumbs Up"},
	37: {Emoji: "\u2602\uFE0F", Description: "Umbrella"},
	38: {Emoji: "\u231B", Description: "Hourglass"},
	39: {Emoji: "\u23F0", Description: "Clock"},
	40: {Emoji: "\U0001F381", Description: "Gift"},
	41: {Emoji: "\U0001F4A1", Description: "Light Bulb"},
	42: {Emoji: "\U0001F4D5", Description: "Book"},
	43: {Emoji: "\u270F\uFE0F", Description: "Pencil"},
	44: {Emoji: "\U0001F4CE", Description: "Paperclip"},
	45: {Emoji: "\u2702\uFE0F", Description: "Scissors"},
	46: {Emoji: "\U0001F512", Description: "Lock"},
	47: {Emoji: "\U0001F511", Description: "Key"},
	48: {Emoji: "\U0001F528", Description: "Hammer"},
	49: {Emoji: "\u260E\uFE0F", Description: "Telephone"},
	50: {Emoji: "\U0001F3C1", Description: "Flag"},
	51: {Emoji: "\U0001F682", Description: "Train"},
	52: {Emoji: "\U0001F6B2", Description: "Bicycle"},
	53: {Emoji: "\u2708\uFE0F", Description: "Aeroplane"},
	54: {Emoji: "\U0001F680", Description: "Rocket"},
	55: {Emoji: "\U0001F3C6", Description: "Trophy"},
	56: {Emoji: "\u26BD", Description: "Ball"},
	57: {Emoji: "\U0001F3B8", Description: "Guitar"},
	58: {Emoji: "\U0001F3BA", Description: "Trumpet"},
	59: {Emoji: "\U0001F514", Description: "Bell"},
	60: {Emoji: "\u2693", Description: "Anchor"},
	61: {Emoji: "\U0001F3A7", Description: "Headphones"},
	62: {Emoji: "\U0001F4C1", Description: "Folder"},
	63: {Emoji: "\U0001F4CC", Description: "Pin"},
}

func (vs *VerificationState) GetEmojiDescriptions() []SASEmojiDescription {
	vs.mu.RLock()
	defer vs.mu.RUnlock()
	result := make([]SASEmojiDescription, len(vs.Emojis))
	for i, r := range vs.Emojis {
		if desc, ok := sasEmojiTable[r]; ok {
			result[i] = desc
		} else {
			result[i] = SASEmojiDescription{Emoji: string(r), Description: fmt.Sprintf("Unknown (%d)", r)}
		}
	}
	return result
}

type MatrixClient struct {
	mu     sync.RWMutex
	client *mautrix.Client
	crypto *cryptohelper.CryptoHelper
	cfg    *Config
	store  *Store
	wsHub  *WSHub

	cancelSync context.CancelFunc
	syncing    bool
	userID     id.UserID

	uploads     map[string]string
	uploadsLock sync.Mutex
	uploadSeq   int

	loginRequestID string
	displayName    string
	verifyHelper   *verificationhelper.VerificationHelper
	verifyState  VerificationState
}

func NewMatrixClient(cfg *Config, store *Store, wsHub *WSHub) *MatrixClient {
	return &MatrixClient{
		cfg:     cfg,
		store:   store,
		wsHub:   wsHub,
		uploads: make(map[string]string),
	}
}

func (mc *MatrixClient) beeperRequest(ctx context.Context, method, path string, body interface{}) ([]byte, int, error) {
	var reqBody io.Reader
	if body != nil {
		data, _ := json.Marshal(body)
		reqBody = bytes.NewReader(data)
	} else {
		reqBody = bytes.NewReader([]byte("{}"))
	}

	req, err := http.NewRequestWithContext(ctx, method, BeeperAPIBase+path, reqBody)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer BEEPER-PRIVATE-API-PLEASE-DONT-USE")

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	respBody, _ := io.ReadAll(resp.Body)
	return respBody, resp.StatusCode, nil
}

func (mc *MatrixClient) StartLogin(ctx context.Context, email string) error {
	body, status, err := mc.beeperRequest(ctx, "POST", "/user/login", nil)
	if err != nil {
		return fmt.Errorf("initiating login: %w", err)
	}
	if status != http.StatusOK && status != http.StatusCreated {
		return fmt.Errorf("login initiate failed (HTTP %d): %s", status, string(body))
	}

	var initResp struct {
		Request string   `json:"request"`
		Type    []string `json:"type"`
	}
	if err := json.Unmarshal(body, &initResp); err != nil {
		return fmt.Errorf("parsing login init response: %w", err)
	}
	if initResp.Request == "" {
		return fmt.Errorf("no request ID in login response: %s", string(body))
	}

	mc.loginRequestID = initResp.Request
	log.Info().Str("requestID", mc.loginRequestID).Msg("Login initiated, sending email...")

	emailReq := map[string]interface{}{
		"request":                mc.loginRequestID,
		"email":                  email,
		"app_type":               "bbctl",
		"only_existing_accounts": true,
	}
	body, status, err = mc.beeperRequest(ctx, "POST", "/user/login/email", emailReq)
	if err != nil {
		return fmt.Errorf("sending login email: %w", err)
	}
	if status != http.StatusOK && status != http.StatusCreated && status != http.StatusNoContent {
		return fmt.Errorf("login email failed (HTTP %d): %s", status, string(body))
	}

	log.Info().Str("email", email).Msg("Verification code sent to email")
	return nil
}

func (mc *MatrixClient) CompleteLogin(ctx context.Context, email, code string) (string, error) {
	if mc.loginRequestID == "" {
		return "", fmt.Errorf("no login in progress — call StartLogin first")
	}

	verifyReq := map[string]interface{}{
		"request":                mc.loginRequestID,
		"response":              code,
		"app_type":              "bbctl",
		"only_existing_accounts": true,
	}
	body, status, err := mc.beeperRequest(ctx, "POST", "/user/login/response", verifyReq)
	if err != nil {
		return "", fmt.Errorf("sending verify request: %w", err)
	}
	if status != http.StatusOK && status != http.StatusCreated {
		return "", fmt.Errorf("verify failed (HTTP %d): %s", status, string(body))
	}

	var loginResp struct {
		Token      string          `json:"token"`
		LoginToken string          `json:"loginToken"`
		JWT        string          `json:"jwt"`
		WhoAmI     json.RawMessage `json:"whoami"`
	}
	if err := json.Unmarshal(body, &loginResp); err != nil {
		return "", fmt.Errorf("parsing login response: %w", err)
	}

	jwtToken := loginResp.Token
	if jwtToken == "" {
		jwtToken = loginResp.LoginToken
	}
	if jwtToken == "" {
		jwtToken = loginResp.JWT
	}
	if jwtToken == "" {
		return "", fmt.Errorf("no token in login response: %s", string(body))
	}

	mc.loginRequestID = ""

	mc.seedAccountsFromWhoAmI(loginResp.WhoAmI)

	if len(loginResp.WhoAmI) > 0 {
		whoamiPath := filepath.Join(mc.cfg.DataDir(), "whoami.json")
		os.WriteFile(whoamiPath, loginResp.WhoAmI, 0600)
	}

	log.Info().Msg("Got JWT token from Beeper API, logging into Matrix...")

	client, err := mautrix.NewClient(BeeperHomeserver, "", "")
	if err != nil {
		return "", fmt.Errorf("creating Matrix client: %w", err)
	}

	cryptoHelper, err := cryptohelper.NewCryptoHelper(client, []byte("beeper-sidecar-pickle"), mc.cfg.CryptoDBPath())
	if err != nil {
		return "", fmt.Errorf("creating crypto helper: %w", err)
	}

	cryptoHelper.LoginAs = &mautrix.ReqLogin{
		Type: "org.matrix.login.jwt",
		Identifier: mautrix.UserIdentifier{
			Type: mautrix.IdentifierTypeUser,
		},
		Token:                    jwtToken,
		InitialDeviceDisplayName: DeviceDisplayName,
	}

	if err := cryptoHelper.Init(ctx); err != nil {
		return "", fmt.Errorf("crypto init (login): %w", err)
	}

	// jwt login doesn't always set client.Crypto
	if client.Crypto == nil {
		client.Crypto = cryptoHelper
	}

	mc.mu.Lock()
	mc.client = client
	mc.crypto = cryptoHelper
	mc.userID = client.UserID
	mc.mu.Unlock()

	if err := mc.cfg.SaveSession(
		BeeperHomeserver,
		client.AccessToken,
		string(client.UserID),
		string(client.DeviceID),
	); err != nil {
		return "", fmt.Errorf("saving session: %w", err)
	}

	log.Info().
		Str("userID", string(client.UserID)).
		Str("deviceID", string(client.DeviceID)).
		Msg("Matrix login successful")

	mc.registerEventHandlers()
	mc.initVerificationHelper(ctx)

	go mc.StartSync(context.Background())

	return mc.cfg.GetLocalAPIToken(), nil
}

func (mc *MatrixClient) Resume(ctx context.Context) error {
	if !mc.cfg.HasSession() {
		return fmt.Errorf("no saved session")
	}

	client, err := mautrix.NewClient(
		mc.cfg.Session.HomeserverURL,
		id.UserID(mc.cfg.Session.UserID),
		mc.cfg.Session.AccessToken,
	)
	if err != nil {
		return fmt.Errorf("creating Matrix client: %w", err)
	}
	client.DeviceID = id.DeviceID(mc.cfg.Session.DeviceID)

	cryptoHelper, err := cryptohelper.NewCryptoHelper(client, []byte("beeper-sidecar-pickle"), mc.cfg.CryptoDBPath())
	if err != nil {
		return fmt.Errorf("creating crypto helper: %w", err)
	}

	if err := cryptoHelper.Init(ctx); err != nil {
		return fmt.Errorf("crypto init (resume): %w", err)
	}

	// crypto init may not set client.Crypto on resume
	if client.Crypto == nil {
		client.Crypto = cryptoHelper
	}

	mc.mu.Lock()
	mc.client = client
	mc.crypto = cryptoHelper
	mc.userID = client.UserID
	mc.mu.Unlock()

	mc.registerEventHandlers()
	mc.initVerificationHelper(ctx)

	go mc.fetchAndSeedAccounts()

	return nil
}

func (mc *MatrixClient) StartSync(ctx context.Context) {
	mc.mu.Lock()
	if mc.syncing {
		mc.mu.Unlock()
		return
	}
	mc.syncing = true

	syncCtx, cancel := context.WithCancel(ctx)
	mc.cancelSync = cancel
	mc.mu.Unlock()

	if mc.store.RoomCount() == 0 && mc.client.Store != nil {
		log.Info().Msg("No persisted rooms — forcing full initial sync")
		mc.client.Store.SaveNextBatch(ctx, mc.userID, "")
	}

	log.Info().Int("rooms", mc.store.RoomCount()).Msg("Starting Matrix sync loop...")

	go mc.waitForTagsAndNotify(syncCtx)

	for {
		err := mc.client.SyncWithContext(syncCtx)
		if syncCtx.Err() != nil {
				break
		}
		if err != nil {
			log.Error().Err(err).Msg("Sync error, reconnecting in 5 seconds...")
			select {
			case <-syncCtx.Done():
				break
			case <-time.After(5 * time.Second):
				continue
			}
		}
	}

	mc.mu.Lock()
	mc.syncing = false
	mc.mu.Unlock()
	log.Info().Msg("Sync loop stopped")
}

func (mc *MatrixClient) Stop() {
	mc.mu.Lock()
	if mc.cancelSync != nil {
		mc.cancelSync()
	}
	if mc.crypto != nil {
		mc.crypto.Close()
	}
	mc.mu.Unlock()
}

func (mc *MatrixClient) IsLoggedIn() bool {
	mc.mu.RLock()
	defer mc.mu.RUnlock()
	return mc.client != nil && mc.client.AccessToken != ""
}

func (mc *MatrixClient) VerificationRequested(ctx context.Context, txnID id.VerificationTransactionID, from id.UserID, fromDevice id.DeviceID) {
	log.Info().
		Str("txnID", string(txnID)).
		Str("from", string(from)).
		Msg("Verification requested")

	mc.verifyState.mu.Lock()
	defer mc.verifyState.mu.Unlock()

	mc.verifyState.Active = true
	mc.verifyState.TxnID = string(txnID)
	mc.verifyState.Status = "requested"
	mc.verifyState.Emojis = nil
	mc.verifyState.Decimals = nil
	mc.verifyState.Error = ""

	go func() {
		if err := mc.verifyHelper.AcceptVerification(context.Background(), txnID); err != nil {
			log.Error().Err(err).Msg("Failed to accept verification")
			mc.verifyState.mu.Lock()
			mc.verifyState.Status = "error"
			mc.verifyState.Error = err.Error()
			mc.verifyState.mu.Unlock()
		}
	}()
}

func (mc *MatrixClient) VerificationReady(ctx context.Context, txnID id.VerificationTransactionID, otherDeviceID id.DeviceID, supportsSAS, supportsScanQRCode bool, qrCode *verificationhelper.QRCode) {
	log.Info().
		Str("txnID", string(txnID)).
		Str("otherDevice", string(otherDeviceID)).
		Bool("supportsSAS", supportsSAS).
		Msg("Verification ready — other device accepted")

	mc.verifyState.mu.Lock()
	mc.verifyState.Status = "ready"
	mc.verifyState.mu.Unlock()

	if supportsSAS {
		go func() {
			if err := mc.verifyHelper.StartSAS(context.Background(), txnID); err != nil {
				log.Error().Err(err).Msg("Failed to start SAS")
				mc.verifyState.mu.Lock()
				mc.verifyState.Status = "error"
				mc.verifyState.Error = err.Error()
				mc.verifyState.mu.Unlock()
			}
		}()
	}
}

func (mc *MatrixClient) VerificationCancelled(ctx context.Context, txnID id.VerificationTransactionID, code event.VerificationCancelCode, reason string) {
	log.Warn().
		Str("txnID", string(txnID)).
		Str("code", string(code)).
		Str("reason", reason).
		Msg("Verification cancelled")

	mc.verifyState.mu.Lock()
	defer mc.verifyState.mu.Unlock()
	mc.verifyState.Status = "cancelled"
	mc.verifyState.Error = fmt.Sprintf("%s: %s", code, reason)
}

func (mc *MatrixClient) VerificationDone(ctx context.Context, txnID id.VerificationTransactionID, method event.VerificationMethod) {
	log.Info().
		Str("txnID", string(txnID)).
		Msg("Verification done!")

	mc.verifyState.mu.Lock()
	mc.verifyState.Status = "done"
	mc.verifyState.mu.Unlock()

	go mc.requestSecretsAndImportBackup()
}

func (mc *MatrixClient) requestSecretsAndImportBackup() {
	mc.mu.RLock()
	cryptoH := mc.crypto
	mc.mu.RUnlock()
	if cryptoH == nil {
		return
	}
	machine := cryptoH.Machine()
	if machine == nil {
		return
	}

	ctx := context.Background()

	secretNames := []id.Secret{
		"m.cross_signing.master",
		"m.cross_signing.self_signing",
		"m.cross_signing.user_signing",
		"m.megolm_backup.v1",
	}

	for _, name := range secretNames {
		log.Info().Str("secret", string(name)).Msg("Requesting secret from other devices...")
		err := machine.GetOrRequestSecret(ctx, name, func(secret string) (bool, error) {
			log.Info().Str("secret", string(name)).Int("len", len(secret)).Msg("Received secret from other device")
			machine.CryptoStore.PutSecret(ctx, name, secret)
			return true, nil
		}, 30*time.Second)
		if err != nil {
			log.Warn().Err(err).Str("secret", string(name)).Msg("Failed to get secret")
		}
	}

	log.Info().Msg("Checking if we got the backup key...")
	backupSecret, err := machine.CryptoStore.GetSecret(ctx, "m.megolm_backup.v1")
	if err != nil || backupSecret == "" {
		log.Warn().Msg("Backup key not received from other device — old messages won't decrypt yet")
		return
	}

	log.Info().Int("len", len(backupSecret)).Msg("Got backup key, attempting to download key backup...")
	backupKeyBytes, err := base64.StdEncoding.DecodeString(backupSecret)
	if err != nil {
		backupKeyBytes, err = base64.RawStdEncoding.DecodeString(backupSecret)
	}
	if err != nil {
		log.Warn().Err(err).Msg("Failed to decode backup key")
		return
	}
	if len(backupKeyBytes) > 32 {
		backupKeyBytes = backupKeyBytes[:32]
	}

	megolmBackupKey, err := backup.MegolmBackupKeyFromBytes(backupKeyBytes)
	if err != nil {
		log.Warn().Err(err).Msg("Failed to create backup key")
		return
	}

	version, err := machine.DownloadAndStoreLatestKeyBackup(ctx, megolmBackupKey)
	if err != nil {
		log.Warn().Err(err).Msg("Failed to download key backup")
		return
	}

	log.Info().Str("version", string(version)).Msg("Key backup downloaded successfully")

	masterSecret, _ := machine.CryptoStore.GetSecret(ctx, "m.cross_signing.master")
	selfSignSecret, _ := machine.CryptoStore.GetSecret(ctx, "m.cross_signing.self_signing")
	userSignSecret, _ := machine.CryptoStore.GetSecret(ctx, "m.cross_signing.user_signing")

	if masterSecret != "" && selfSignSecret != "" && userSignSecret != "" {
		masterSeed, err1 := decodeBase64Flexible(masterSecret)
		selfSeed, err2 := decodeBase64Flexible(selfSignSecret)
		userSeed, err3 := decodeBase64Flexible(userSignSecret)
		if err1 == nil && err2 == nil && err3 == nil {
			if len(masterSeed) >= 32 && len(selfSeed) >= 32 && len(userSeed) >= 32 {
				err := machine.ImportCrossSigningKeys(crypto.CrossSigningSeeds{
					MasterKey:      masterSeed[:32],
					SelfSigningKey: selfSeed[:32],
					UserSigningKey: userSeed[:32],
				})
				if err != nil {
					log.Warn().Err(err).Msg("Failed to import received cross-signing keys")
				} else {
					log.Info().Msg("Cross-signing keys imported from secret sharing!")
				}
			}
		}
	}
}

func (mc *MatrixClient) ShowSAS(ctx context.Context, txnID id.VerificationTransactionID, emojis []rune, emojiDescriptions []string, decimals []int) {
	log.Info().
		Str("txnID", string(txnID)).
		Int("emojiCount", len(emojis)).
		Ints("decimals", decimals).
		Msg("SAS emojis ready for display")

	mc.verifyState.mu.Lock()
	defer mc.verifyState.mu.Unlock()
	mc.verifyState.TxnID = string(txnID)
	mc.verifyState.Status = "emojis_ready"
	mc.verifyState.Emojis = emojis
	mc.verifyState.Decimals = decimals
}

func (mc *MatrixClient) initVerificationHelper(ctx context.Context) {
	mc.mu.RLock()
	client := mc.client
	cryptoHelper := mc.crypto
	mc.mu.RUnlock()

	if client == nil || cryptoHelper == nil {
		log.Warn().Msg("Cannot init verification helper: client or crypto not ready")
		return
	}

	machine := cryptoHelper.Machine()
	if machine == nil {
		log.Warn().Msg("Cannot init verification helper: OlmMachine is nil")
		return
	}

	if client.Crypto == nil {
		log.Warn().Msg("Cannot init verification helper: client.Crypto is nil")
		return
	}

	defer func() {
		if r := recover(); r != nil {
			log.Error().Interface("panic", r).Msg("Panic in verification helper init (recovered)")
		}
	}()

	vh := verificationhelper.NewVerificationHelper(client, machine, verificationhelper.NewInMemoryVerificationStore(), mc, false, false, true)
	if err := vh.Init(context.Background()); err != nil {
		log.Error().Err(err).Msg("Failed to initialize verification helper")
		return
	}

	mc.mu.Lock()
	mc.verifyHelper = vh
	mc.mu.Unlock()

	log.Info().Msg("Verification helper initialized")
}

func (mc *MatrixClient) ensureVerificationHelper(ctx context.Context) error {
	mc.mu.RLock()
	vh := mc.verifyHelper
	mc.mu.RUnlock()

	if vh != nil {
		return nil
	}

	mc.initVerificationHelper(ctx)

	mc.mu.RLock()
	vh = mc.verifyHelper
	mc.mu.RUnlock()

	if vh == nil {
		return fmt.Errorf("verification helper could not be initialized — crypto may not be ready yet")
	}
	return nil
}

func (mc *MatrixClient) StartDeviceVerification(ctx context.Context) (string, error) {
	if err := mc.ensureVerificationHelper(ctx); err != nil {
		return "", err
	}

	mc.mu.RLock()
	vh := mc.verifyHelper
	userID := mc.userID
	mc.mu.RUnlock()

	if vh == nil {
		return "", fmt.Errorf("verification helper not initialized — try again after sync completes")
	}

	mc.verifyState.mu.Lock()
	mc.verifyState.Active = true
	mc.verifyState.Status = "requested"
	mc.verifyState.Emojis = nil
	mc.verifyState.Decimals = nil
	mc.verifyState.Error = ""
	mc.verifyState.mu.Unlock()

	txnID, err := vh.StartVerification(context.Background(), userID)
	if err != nil {
		mc.verifyState.mu.Lock()
		mc.verifyState.Active = false
		mc.verifyState.Status = "error"
		mc.verifyState.Error = err.Error()
		mc.verifyState.mu.Unlock()
		return "", fmt.Errorf("starting verification: %w", err)
	}

	mc.verifyState.mu.Lock()
	mc.verifyState.TxnID = string(txnID)
	mc.verifyState.mu.Unlock()

	log.Info().Str("txnID", string(txnID)).Msg("Self-verification started")
	return string(txnID), nil
}

func (mc *MatrixClient) ConfirmDeviceVerification(ctx context.Context) error {
	mc.mu.RLock()
	vh := mc.verifyHelper
	mc.mu.RUnlock()

	if vh == nil {
		return fmt.Errorf("verification helper not initialized")
	}

	mc.verifyState.mu.RLock()
	txnID := mc.verifyState.TxnID
	mc.verifyState.mu.RUnlock()

	if txnID == "" {
		return fmt.Errorf("no active verification to confirm")
	}

	return vh.ConfirmSAS(context.Background(), id.VerificationTransactionID(txnID))
}

func (mc *MatrixClient) GetVerificationStatus() map[string]interface{} {
	mc.verifyState.mu.RLock()
	defer mc.verifyState.mu.RUnlock()

	result := map[string]interface{}{
		"active": mc.verifyState.Active,
		"status": mc.verifyState.Status,
		"txnID":  mc.verifyState.TxnID,
	}

	if mc.verifyState.Error != "" {
		result["error"] = mc.verifyState.Error
	}

	if mc.verifyState.Status == "emojis_ready" && len(mc.verifyState.Emojis) > 0 {
		emojis := mc.verifyState.GetEmojiDescriptions()
		result["emojis"] = emojis
		result["decimals"] = mc.verifyState.Decimals
	}

	return result
}

func (mc *MatrixClient) registerEventHandlers() {
	syncer := mc.client.Syncer.(*mautrix.DefaultSyncer)

	syncer.OnEventType(event.EventMessage, mc.handleMessage)
	syncer.OnEventType(event.EventReaction, mc.handleReaction)
	syncer.OnEventType(event.EventRedaction, mc.handleRedaction)
	syncer.OnEventType(event.StateMember, mc.handleMember)
	syncer.OnEventType(event.StateRoomName, mc.handleRoomName)
	syncer.OnEventType(event.StateRoomAvatar, mc.handleRoomAvatar)
	syncer.OnEventType(event.StateEncryption, mc.handleEncryption)
	syncer.OnEventType(event.StateTopic, mc.handleTopic)
	syncer.OnEventType(event.EphemeralEventReceipt, mc.handleReceipt)
	syncer.OnEventType(event.AccountDataRoomTags, mc.handleRoomTags)

	syncer.OnEventType(event.EphemeralEventTyping, mc.handleTyping)

	syncer.OnSync(mc.onSyncResponse)

	log.Debug().Msg("Event handlers registered")
}

func (mc *MatrixClient) handleMessage(ctx context.Context, evt *event.Event) {
	roomID := string(evt.RoomID)
	room := mc.store.EnsureRoom(roomID)

	msg := mc.eventToMessage(evt, room)
	if msg == nil {
		return
	}

	room.Preview = msg
	room.LastActivity = msg.Timestamp

	room.mu.Lock()
	room.Timeline = append(room.Timeline, msg)
	if len(room.Timeline) > 100 {
		room.Timeline = room.Timeline[len(room.Timeline)-100:]
	}
	room.mu.Unlock()

	mc.store.SetRoom(room)
	mc.store.SaveMessage(msg)

	mc.detectRoomBridge(room)

	mc.wsHub.Broadcast(WSEvent{
		Type: "message.upserted",
		Data: msg,
	})
	mc.wsHub.Broadcast(WSEvent{
		Type: "chat.upserted",
		Data: room,
	})

	log.Debug().
		Str("room", roomID).
		Str("sender", string(evt.Sender)).
		Str("type", msg.Type).
		Msg("Message received")
}

func (mc *MatrixClient) handleReaction(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsReaction()
	if content == nil || content.RelatesTo.EventID == "" {
		return
	}

	log.Debug().
		Str("room", string(evt.RoomID)).
		Str("target", string(content.RelatesTo.EventID)).
		Str("key", content.RelatesTo.Key).
		Msg("Reaction received")

	mc.wsHub.Broadcast(WSEvent{
		Type: "message.upserted",
		Data: map[string]interface{}{
			"roomID":      string(evt.RoomID),
			"eventID":     string(content.RelatesTo.EventID),
			"reactionKey": content.RelatesTo.Key,
			"sender":      string(evt.Sender),
		},
	})
}

func (mc *MatrixClient) handleRedaction(ctx context.Context, evt *event.Event) {
	log.Debug().
		Str("room", string(evt.RoomID)).
		Str("redacts", string(evt.Redacts)).
		Msg("Redaction received")

	mc.wsHub.Broadcast(WSEvent{
		Type: "message.deleted",
		Data: map[string]string{
			"chatID":    string(evt.RoomID),
			"messageID": string(evt.Redacts),
		},
	})
}

func (mc *MatrixClient) handleMember(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsMember()
	if content == nil {
		return
	}

	roomID := string(evt.RoomID)
	room := mc.store.EnsureRoom(roomID)
	room.SelfUserID = string(mc.userID)

	targetUser := string(*evt.StateKey)
	member := &Member{
		UserID:      targetUser,
		DisplayName: content.Displayname,
		AvatarURL:   string(content.AvatarURL),
		Membership:  string(content.Membership),
	}
	room.SetMember(targetUser, member)
	mc.store.SaveMember(roomID, member)
	room.mu.RLock()
	room.MemberCount = countJoinedMembers(room.Members)
	joined := countJoinedMembers(room.Members)
	room.mu.RUnlock()

	if joined <= 2 {
		room.Type = "single"
	} else {
		room.Type = "group"
	}

	if room.Type == "single" && room.Title == "" {
		for _, m := range room.Members {
			if m.UserID != string(mc.userID) && m.Membership == "join" {
				room.Title = m.DisplayName
				if room.Title == "" {
					room.Title = m.UserID
				}
				room.DirectUserID = m.UserID
				break
			}
		}
	}

	mc.store.SetRoom(room)

	mc.detectRoomBridge(room)
}

func (mc *MatrixClient) handleRoomName(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsRoomName()
	if content == nil {
		return
	}
	room := mc.store.EnsureRoom(string(evt.RoomID))
	room.Title = content.Name
	mc.store.SetRoom(room)

	mc.wsHub.Broadcast(WSEvent{
		Type: "chat.upserted",
		Data: room,
	})
}

func (mc *MatrixClient) handleRoomAvatar(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsRoomAvatar()
	if content == nil {
		return
	}
	room := mc.store.EnsureRoom(string(evt.RoomID))
	room.AvatarURL = string(content.URL)
	mc.store.SetRoom(room)
}

func (mc *MatrixClient) handleEncryption(ctx context.Context, evt *event.Event) {
	room := mc.store.EnsureRoom(string(evt.RoomID))
	room.Encrypted = true
	mc.store.SetRoom(room)
}

func (mc *MatrixClient) handleTopic(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsTopic()
	if content == nil {
		return
	}
	room := mc.store.EnsureRoom(string(evt.RoomID))
	room.Topic = content.Topic
	mc.store.SetRoom(room)
}

func (mc *MatrixClient) handleReceipt(ctx context.Context, evt *event.Event) {
}

func (mc *MatrixClient) waitForTagsAndNotify(ctx context.Context) {
	for i := 0; i < 15; i++ {
		time.Sleep(2 * time.Second)
		if ctx.Err() != nil {
			return
		}

		pinned := 0
		mc.store.mu.RLock()
		for _, r := range mc.store.rooms {
			if r.IsPinned {
				pinned++
			}
		}
		mc.store.mu.RUnlock()

		if pinned > 0 {
			log.Info().Int("pinned", pinned).Msg("Room tags detected, notifying clients")
			mc.wsHub.Broadcast(WSEvent{Type: "tags.updated", Data: map[string]int{"pinned": pinned}})
			time.Sleep(5 * time.Second)
			mc.store.mu.RLock()
			pinned2 := 0
			for _, r := range mc.store.rooms {
				if r.IsPinned {
					pinned2++
				}
			}
			mc.store.mu.RUnlock()
			if pinned2 > pinned {
				log.Info().Int("pinned", pinned2).Msg("More tags arrived, notifying again")
				mc.wsHub.Broadcast(WSEvent{Type: "tags.updated", Data: map[string]int{"pinned": pinned2}})
			}
			return
		}
	}
}

func (mc *MatrixClient) handleRoomTags(ctx context.Context, evt *event.Event) {
	content := evt.Content.AsTag()
	if content == nil {
		log.Warn().Str("roomID", string(evt.RoomID)).Msg("Room tags event with nil content")
		return
	}

	room := mc.store.EnsureRoom(string(evt.RoomID))

	_, room.IsPinned = content.Tags["m.favourite"]
	_, room.IsLowPriority = content.Tags["m.lowpriority"]

	room.Tags = make([]string, 0)
	for tag := range content.Tags {
		room.Tags = append(room.Tags, string(tag))
	}

	mc.store.SetRoom(room)

	if room.IsPinned {
		mc.wsHub.Broadcast(WSEvent{Type: "tags.updated", Data: map[string]interface{}{"roomID": room.ID, "isPinned": true}})
	}
}

func (mc *MatrixClient) handleTyping(ctx context.Context, evt *event.Event) {
}

func (mc *MatrixClient) onSyncResponse(ctx context.Context, resp *mautrix.RespSync, since string) bool {
	if resp.NextBatch != "" {
		mc.cfg.SaveSyncToken(resp.NextBatch)
	}

	for roomID, roomData := range resp.Rooms.Join {
		room := mc.store.EnsureRoom(string(roomID))

		if roomData.UnreadNotifications != nil {
			room.UnreadCount = roomData.UnreadNotifications.NotificationCount
		}

		for _, evt := range roomData.State.Events {
			evt.RoomID = roomID
			mc.processStateEvent(evt)
		}

		var latestTimestamp time.Time
		for _, evt := range roomData.Timeline.Events {
			evt.RoomID = roomID
			if evt.StateKey != nil {
				mc.processStateEvent(evt)
			}
			evtTime := time.UnixMilli(evt.Timestamp)
			if evtTime.After(latestTimestamp) {
				latestTimestamp = evtTime
			}
		}

		room = mc.store.EnsureRoom(string(roomID))

		if room.LastActivity.IsZero() || latestTimestamp.After(room.LastActivity) {
			if !latestTimestamp.IsZero() {
				room.LastActivity = latestTimestamp
			}
		}

		if roomData.Timeline.PrevBatch != "" {
			room.TimelineEnd = roomData.Timeline.PrevBatch
		}

		mc.detectRoomBridge(room)

		if room.Title == "" && room.Type == "single" {
			room.Title = mc.deriveDMTitle(room)
		}
		if room.Title == "" {
			if room.CanonicalAlias != "" {
				room.Title = room.CanonicalAlias
			}
		}

		mc.store.SetRoom(room)
	}

	for roomID := range resp.Rooms.Invite {
		room := mc.store.EnsureRoom(string(roomID))
		room.Type = "single"
		mc.store.SetRoom(room)
	}

	for roomID := range resp.Rooms.Leave {
		mc.store.DeleteRoom(string(roomID))
	}

	log.Debug().
		Int("joined", len(resp.Rooms.Join)).
		Int("totalRooms", mc.store.RoomCount()).
		Str("since", since).
		Msg("Sync processed")

	return true
}

func (mc *MatrixClient) processStateEvent(evt *event.Event) {
	if err := evt.Content.ParseRaw(evt.Type); err != nil {
		return
	}

	room := mc.store.EnsureRoom(string(evt.RoomID))

	switch evt.Type {
	case event.StateRoomName:
		if c := evt.Content.AsRoomName(); c != nil {
			room.Title = c.Name
		}
	case event.StateMember:
		if c := evt.Content.AsMember(); c != nil && evt.StateKey != nil {
			m := &Member{
				UserID:      *evt.StateKey,
				DisplayName: c.Displayname,
				AvatarURL:   string(c.AvatarURL),
				Membership:  string(c.Membership),
			}
			room.SetMember(*evt.StateKey, m)
			mc.store.SaveMember(string(evt.RoomID), m)
		}
	case event.StateRoomAvatar:
		if c := evt.Content.AsRoomAvatar(); c != nil {
			room.AvatarURL = string(c.URL)
		}
	case event.StateEncryption:
		room.Encrypted = true
	case event.StateTopic:
		if c := evt.Content.AsTopic(); c != nil {
			room.Topic = c.Topic
		}
	}

	mc.store.SetRoom(room)
}

func (mc *MatrixClient) eventToMessage(evt *event.Event, room *Room) *Message {
	content := evt.Content.AsMessage()
	if content == nil {
		return nil
	}

	msg := &Message{
		ID:        string(evt.ID),
		EventID:   string(evt.ID),
		ChatID:    string(evt.RoomID),
		AccountID: room.AccountID,
		SenderID:  string(evt.Sender),
		Timestamp: time.Unix(0, int64(evt.Timestamp)*int64(time.Millisecond)),
		SortKey:   strconv.FormatInt(int64(evt.Timestamp), 10),
		IsSender:  evt.Sender == mc.userID,
	}

	room.mu.RLock()
	if member, ok := room.Members[string(evt.Sender)]; ok {
		msg.SenderName = member.DisplayName
	}
	room.mu.RUnlock()
	if msg.SenderName == "" {
		msg.SenderName = string(evt.Sender)
	}

	switch content.MsgType {
	case event.MsgText, event.MsgNotice, event.MsgEmote:
		msg.Type = "TEXT"
		if content.MsgType == event.MsgNotice {
			msg.Type = "NOTICE"
		}
		msg.Text = content.Body
		if content.FormattedBody != "" {
			msg.Text = content.Body
		}
		// bridges prefix sender name in body
		if msg.SenderName != "" && strings.HasPrefix(msg.Text, msg.SenderName+": ") {
			msg.Text = strings.TrimPrefix(msg.Text, msg.SenderName+": ")
		}

	case event.MsgImage:
		msg.Type = "IMAGE"
		msg.Text = content.Body
		att := mc.contentToAttachment(content, "img")
		msg.Attachments = []*Attachment{att}

	case event.MsgVideo:
		msg.Type = "VIDEO"
		msg.Text = content.Body
		att := mc.contentToAttachment(content, "video")
		msg.Attachments = []*Attachment{att}

	case event.MsgAudio:
		msg.Type = "AUDIO"
		msg.Text = content.Body
		att := mc.contentToAttachment(content, "audio")
		if content.MSC3245Voice != nil {
			att.IsVoiceNote = true
			msg.Type = "VOICE"
		}
		msg.Attachments = []*Attachment{att}

	case event.MsgFile:
		msg.Type = "FILE"
		msg.Text = content.Body
		att := mc.contentToAttachment(content, "unknown")
		msg.Attachments = []*Attachment{att}

	case event.MsgLocation:
		msg.Type = "LOCATION"
		msg.Text = content.Body

	default:
		msg.Type = "TEXT"
		msg.Text = content.Body
	}

	if content.RelatesTo != nil && content.RelatesTo.InReplyTo != nil {
		msg.LinkedMessageID = string(content.RelatesTo.InReplyTo.EventID)
	}

	if content.RelatesTo != nil && content.RelatesTo.Type == event.RelReplace {
		if content.NewContent != nil {
			msg.Text = content.NewContent.Body
			msg.ID = string(content.RelatesTo.EventID)
		}
	}

	return msg
}

func (mc *MatrixClient) contentToAttachment(content *event.MessageEventContent, attType string) *Attachment {
	att := &Attachment{
		Type:     attType,
		MimeType: content.GetInfo().MimeType,
		FileName: content.Body,
		FileSize: int64(content.GetInfo().Size),
		Width:    content.GetInfo().Width,
		Height:   content.GetInfo().Height,
	}

	if content.GetInfo().Duration > 0 {
		att.Duration = float64(content.GetInfo().Duration) / 1000.0
	}

	if content.File != nil {
		att.SrcURL = string(content.File.URL)
		att.ID = string(content.File.URL)
		if fileJSON, err := json.Marshal(content.File); err == nil {
			att.EncryptedFileJSON = string(fileJSON)
		}
	} else if content.URL != "" {
		att.SrcURL = string(content.URL)
		att.ID = string(content.URL)
	}

	if content.GetInfo().MimeType == "image/gif" ||
		(content.MSC1767Audio == nil && strings.Contains(strings.ToLower(content.Body), ".gif")) {
		att.IsGif = true
	}

	if content.MsgType == "m.sticker" {
		att.IsSticker = true
	}

	return att
}

func (mc *MatrixClient) SendMessage(ctx context.Context, roomID, text string, replyTo string) (string, error) {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return "", fmt.Errorf("not logged in")
	}

	content := &event.MessageEventContent{
		MsgType: event.MsgText,
		Body:    text,
	}

	if replyTo != "" {
		content.RelatesTo = &event.RelatesTo{
			InReplyTo: &event.InReplyTo{
				EventID: id.EventID(replyTo),
			},
		}
	}

	resp, err := client.SendMessageEvent(ctx, id.RoomID(roomID), event.EventMessage, content)
	if err != nil {
		return "", fmt.Errorf("sending message: %w", err)
	}

	return string(resp.EventID), nil
}

func (mc *MatrixClient) EditMessage(ctx context.Context, roomID, eventID, newText string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	content := &event.MessageEventContent{
		MsgType: event.MsgText,
		Body:    "* " + newText,
		RelatesTo: &event.RelatesTo{
			Type:    event.RelReplace,
			EventID: id.EventID(eventID),
		},
		NewContent: &event.MessageEventContent{
			MsgType: event.MsgText,
			Body:    newText,
		},
	}

	_, err := client.SendMessageEvent(ctx, id.RoomID(roomID), event.EventMessage, content)
	return err
}

func (mc *MatrixClient) RedactMessage(ctx context.Context, roomID, eventID string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	_, err := client.RedactEvent(ctx, id.RoomID(roomID), id.EventID(eventID))
	return err
}

func (mc *MatrixClient) SendReaction(ctx context.Context, roomID, eventID, emoji string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	content := &event.ReactionEventContent{
		RelatesTo: event.RelatesTo{
			Type:    event.RelAnnotation,
			EventID: id.EventID(eventID),
			Key:     emoji,
		},
	}

	_, err := client.SendMessageEvent(ctx, id.RoomID(roomID), event.EventReaction, content)
	return err
}

func (mc *MatrixClient) RemoveReaction(ctx context.Context, roomID, eventID, reactionKey string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	return fmt.Errorf("reaction removal not yet implemented — requires reaction event tracking")
}

func (mc *MatrixClient) MarkRead(ctx context.Context, roomID string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	room := mc.store.GetRoom(roomID)
	if room == nil || room.Preview == nil {
		return nil
	}

	err := client.SendReceipt(ctx, id.RoomID(roomID), id.EventID(room.Preview.EventID), event.ReceiptTypeRead, nil)
	if err != nil {
		return err
	}

	err = client.SetReadMarkers(ctx, id.RoomID(roomID), &mautrix.ReqSetReadMarkers{
		FullyRead: id.EventID(room.Preview.EventID),
		Read:      id.EventID(room.Preview.EventID),
	})

	room.UnreadCount = 0
	mc.store.SetRoom(room)

	return err
}

func (mc *MatrixClient) SetRoomTag(ctx context.Context, roomID, tag string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	return client.AddTag(ctx, id.RoomID(roomID), event.RoomTag(tag), 0.5)
}

func (mc *MatrixClient) RemoveRoomTag(ctx context.Context, roomID, tag string) error {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return fmt.Errorf("not logged in")
	}

	return client.RemoveTag(ctx, id.RoomID(roomID), event.RoomTag(tag))
}

func (mc *MatrixClient) GetMessages(ctx context.Context, roomID string, limit int, from string, direction rune) ([]*Message, bool, string, error) {
	if from == "" && direction != 'f' {
		room := mc.store.GetRoom(roomID)
		if room != nil {
			room.mu.RLock()
			timeline := make([]*Message, len(room.Timeline))
			copy(timeline, room.Timeline)
			token := room.TimelineEnd
			room.mu.RUnlock()

			if len(timeline) > 0 {
				if len(timeline) > limit {
					timeline = timeline[len(timeline)-limit:]
				}
				return timeline, token != "", token, nil
			}
		}
	}
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return nil, false, "", fmt.Errorf("not logged in")
	}

	if limit <= 0 {
		limit = 20
	}

	dir := mautrix.DirectionBackward
	if direction == 'f' {
		dir = mautrix.DirectionForward
	}

	resp, err := client.Messages(ctx, id.RoomID(roomID), from, "", dir, nil, limit)
	if err != nil {
		return nil, false, "", fmt.Errorf("fetching messages: %w", err)
	}

	room := mc.store.GetRoom(roomID)
	if room == nil {
		room = mc.store.EnsureRoom(roomID)
	}

	messages := make([]*Message, 0, len(resp.Chunk))
	for _, evt := range resp.Chunk {
		if err := evt.Content.ParseRaw(evt.Type); err != nil {
			continue
		}

		if evt.Type == event.EventEncrypted {
			decrypted, err := mc.crypto.Decrypt(ctx, evt)
			if err != nil {
					messages = append(messages, &Message{
					ID:        string(evt.ID),
					EventID:   string(evt.ID),
					ChatID:    string(evt.RoomID),
					AccountID: room.AccountID,
					SenderID:  string(evt.Sender),
					Timestamp: time.Unix(0, int64(evt.Timestamp)*int64(time.Millisecond)),
					SortKey:   strconv.FormatInt(int64(evt.Timestamp), 10),
					IsSender:  evt.Sender == mc.userID,
					Type:      "TEXT",
					Text:      "[Encrypted message — unable to decrypt]",
				})
				continue
			}
			evt = decrypted
		}

		if evt.Type == event.EventMessage {
			msg := mc.eventToMessage(evt, room)
			if msg != nil {
				messages = append(messages, msg)
			}
		}
	}

	hasMore := resp.End != ""

	if from == "" && direction != 'f' && len(messages) > 0 {
		room := mc.store.EnsureRoom(roomID)
		room.mu.Lock()
		room.Timeline = messages
		room.TimelineEnd = resp.End
		room.mu.Unlock()
	}

	return messages, hasMore, resp.End, nil
}

func (mc *MatrixClient) UploadMedia(ctx context.Context, data []byte, fileName, mimeType string) (string, error) {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return "", fmt.Errorf("not logged in")
	}

	resp, err := client.UploadMedia(ctx, mautrix.ReqUploadMedia{
		Content:       bytes.NewReader(data),
		ContentLength: int64(len(data)),
		ContentType:   mimeType,
		FileName:      fileName,
	})
	if err != nil {
		return "", fmt.Errorf("uploading media: %w", err)
	}

	mc.uploadsLock.Lock()
	mc.uploadSeq++
	uploadID := fmt.Sprintf("upload_%d_%d", time.Now().Unix(), mc.uploadSeq)
	mc.uploads[uploadID] = resp.ContentURI.String()
	mc.uploadsLock.Unlock()

	return uploadID, nil
}

func (mc *MatrixClient) GetUploadMXC(uploadID string) (string, bool) {
	mc.uploadsLock.Lock()
	defer mc.uploadsLock.Unlock()
	uri, ok := mc.uploads[uploadID]
	return uri, ok
}

func (mc *MatrixClient) DownloadMedia(ctx context.Context, mxcURI string) ([]byte, string, error) {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return nil, "", fmt.Errorf("not logged in")
	}

	parsed, err := id.ParseContentURI(mxcURI)
	if err != nil {
		return nil, "", fmt.Errorf("parsing mxc URI: %w", err)
	}

	resp, err := client.Download(ctx, parsed)
	if err != nil {
		return nil, "", fmt.Errorf("downloading media: %w", err)
	}
	defer resp.Body.Close()

	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, "", fmt.Errorf("reading media: %w", err)
	}

	contentType := resp.Header.Get("Content-Type")
	return data, contentType, nil
}

func (mc *MatrixClient) CreateRoom(ctx context.Context, name string, isDirect bool, inviteUserIDs []string) (string, error) {
	mc.mu.RLock()
	client := mc.client
	mc.mu.RUnlock()
	if client == nil {
		return "", fmt.Errorf("not logged in")
	}

	invite := make([]id.UserID, len(inviteUserIDs))
	for i, uid := range inviteUserIDs {
		invite[i] = id.UserID(uid)
	}

	req := &mautrix.ReqCreateRoom{
		Invite:   invite,
		IsDirect: isDirect,
		Preset:   "private_chat",
	}
	if name != "" {
		req.Name = name
	}

	resp, err := client.CreateRoom(ctx, req)
	if err != nil {
		return "", fmt.Errorf("creating room: %w", err)
	}

	return string(resp.RoomID), nil
}


func (mc *MatrixClient) fetchAndSeedAccounts() {
	whoamiPath := filepath.Join(mc.cfg.DataDir(), "whoami.json")
	data, err := os.ReadFile(whoamiPath)
	if err != nil {
		log.Debug().Err(err).Msg("No cached whoami data for account seeding")
		return
	}
	log.Info().Int("bytes", len(data)).Msg("Loading accounts from cached whoami")
	mc.seedAccountsFromWhoAmI(data)
}


func (mc *MatrixClient) seedAccountsFromWhoAmI(raw json.RawMessage) {
	if len(raw) == 0 {
		return
	}

	var whoami struct {
		User struct {
			Bridges  map[string]json.RawMessage `json:"bridges"`
			UserInfo struct {
				Username string `json:"username"`
				Email    string `json:"email"`
				FullName string `json:"fullName"`
			} `json:"userInfo"`
		} `json:"user"`
	}
	if err := json.Unmarshal(raw, &whoami); err != nil {
		log.Warn().Err(err).Msg("Failed to parse whoami data")
		return
	}

	if whoami.User.UserInfo.FullName != "" {
		mc.mu.Lock()
		mc.displayName = whoami.User.UserInfo.FullName
		mc.mu.Unlock()
	}

	bridgeNetworkNames := map[string]string{
		"discordgo":    "Discord",
		"facebookgo":   "Facebook Messenger",
		"instagramgo":  "Instagram",
		"gmessages":    "Google Messages",
		"whatsapp":     "WhatsApp",
		"signalgo":     "Signal",
		"signal":       "Signal",
		"telegramgo":   "Telegram",
		"telegram":     "Telegram",
		"linkedin":     "LinkedIn",
		"imessagego":   "iMessage",
		"imessagecloud":"iMessage",
		"slackgo":      "Slack",
		"slack":        "Slack",
		"twittergo":    "Twitter",
		"twitter":      "Twitter",
		"googlechat":   "Google Chat",
		"hungryserv":   "Beeper",
		"sh-line":      "LINE",
	}

	for bridgeID, bridgeData := range whoami.User.Bridges {
		var bridge struct {
			BridgeState struct {
				StateEvent string `json:"stateEvent"`
			} `json:"bridgeState"`
			RemoteState map[string]struct {
				StateEvent string `json:"state_event"`
				RemoteName string `json:"remote_name"`
			} `json:"remoteState"`
		}
		if err := json.Unmarshal(bridgeData, &bridge); err != nil {
			continue
		}

		network := bridgeNetworkNames[bridgeID]
		if network == "" {
			network = bridgeID
		}

		var remoteName string
		for _, rs := range bridge.RemoteState {
			if rs.RemoteName != "" {
				remoteName = rs.RemoteName
				break
			}
		}
		if remoteName == "" {
			remoteName = network
		}

		log.Info().
			Str("bridge", bridgeID).
			Str("network", network).
			Str("state", bridge.BridgeState.StateEvent).
			Str("remoteName", remoteName).
			Msg("Seeded account from whoami")

		mc.store.SetAccount(&Account{
			AccountID: bridgeID,
			Network:   network,
			User: &AccountUser{
				ID:       string(mc.userID),
				FullName: remoteName,
				IsSelf:   true,
			},
		})
	}
}



func (mc *MatrixClient) detectRoomBridge(room *Room) {
	if room.AccountID != "" {
		return
	}

	snapshot := room.GetMembersSnapshot()
	memberIDs := make([]string, 0, len(snapshot))
	for _, m := range snapshot {
		memberIDs = append(memberIDs, m.UserID)
	}

	if info := DetectBridgeFromMembers(memberIDs); info != nil {
		room.AccountID = info.AccountID
		room.Network = info.Network
		mc.ensureAccount(info)
		return
	}

	for _, uid := range memberIDs {
		if uid != string(mc.userID) {
			if info := DetectBridgeFromUserID(uid); info != nil {
				room.AccountID = info.AccountID
				room.Network = info.Network
				mc.ensureAccount(info)
				return
			}
		}
	}

	defaultInfo := DefaultBridgeInfo()
	room.AccountID = defaultInfo.AccountID
	room.Network = defaultInfo.Network
	mc.ensureAccount(&defaultInfo)
}

func (mc *MatrixClient) ensureAccount(info *BridgeInfo) {
	existing := mc.store.GetAccounts()
	for _, a := range existing {
		if a.AccountID == info.AccountID {
			return
		}
	}
}


func (mc *MatrixClient) deriveDMTitle(room *Room) string {
	room.mu.RLock()
	defer room.mu.RUnlock()
	selfID := string(mc.userID)
	for _, m := range room.Members {
		if m.UserID == selfID || m.Membership != "join" {
			continue
		}
		if strings.HasSuffix(m.UserID, "bot:beeper.com") ||
			strings.HasSuffix(m.UserID, "bot:beeper.local") {
			continue
		}
		if m.DisplayName != "" {
			return m.DisplayName
		}
		return m.UserID
	}
	return ""
}

func countJoinedMembers(members map[string]*Member) int {
	count := 0
	for _, m := range members {
		if m.Membership == "join" {
			count++
		}
	}
	return count
}


func (mc *MatrixClient) ImportRecoveryKey(ctx context.Context, recoveryKey string) (int, error) {
	mc.mu.RLock()
	client := mc.client
	crypto := mc.crypto
	mc.mu.RUnlock()

	if client == nil || crypto == nil {
		return 0, fmt.Errorf("not logged in")
	}

	machine := crypto.Machine()
	if machine == nil {
		return 0, fmt.Errorf("crypto not initialized")
	}

	ssssMachine := ssss.NewSSSSMachine(client)

	defaultKeyID, err := ssssMachine.GetDefaultKeyID(ctx)
	if err != nil {
		return 0, fmt.Errorf("fetching default SSSS key ID: %w", err)
	}
	if defaultKeyID == "" {
		return 0, fmt.Errorf("no default SSSS key configured on server")
	}

	log.Info().Str("keyID", string(defaultKeyID)).Msg("Found default SSSS key")

	keyData, err := ssssMachine.GetKeyData(ctx, defaultKeyID)
	if err != nil {
		return 0, fmt.Errorf("fetching SSSS key metadata: %w", err)
	}

	key, err := keyData.VerifyRecoveryKey(string(defaultKeyID), recoveryKey)
	if err != nil {
		return 0, fmt.Errorf("invalid recovery key: %w", err)
	}

	log.Info().Msg("Recovery key verified successfully")

	if err := machine.FetchCrossSigningKeysFromSSSS(ctx, key); err != nil {
		log.Warn().Err(err).Msg("Failed to fetch cross-signing keys (continuing with backup import)")
	} else {
		log.Info().Msg("Cross-signing keys imported — device is now trusted")
	}

	backupKeyData, err := ssssMachine.GetDecryptedAccountData(ctx, event.AccountDataMegolmBackupKey, key)
	if err != nil {
		return 0, fmt.Errorf("fetching backup key from SSSS: %w", err)
	}

	var backupKeyInfo struct {
		Key []byte `json:"key"`
	}
	if err := json.Unmarshal(backupKeyData, &backupKeyInfo); err != nil {
		return 0, fmt.Errorf("parsing backup key: %w", err)
	}

	megolmBackupKey, err := backup.MegolmBackupKeyFromBytes(backupKeyInfo.Key)
	if err != nil {
		return 0, fmt.Errorf("creating backup key: %w", err)
	}

	log.Info().Msg("Backup key extracted from SSSS, downloading key backup...")

	version, err := machine.DownloadAndStoreLatestKeyBackup(ctx, megolmBackupKey)
	if err != nil {
		return 0, fmt.Errorf("downloading key backup: %w", err)
	}

	log.Info().Str("version", string(version)).Msg("Key backup imported successfully")

	return 0, nil
}


func decodeBase64Flexible(s string) ([]byte, error) {
	if b, err := base64.StdEncoding.DecodeString(s); err == nil {
		return b, nil
	}
	return base64.RawStdEncoding.DecodeString(s)
}

func extractSeed32(raw []byte) ([]byte, error) {
	secretStr := string(raw)

	decoded, err := base64.RawStdEncoding.DecodeString(secretStr)
	if err != nil {
		decoded, err = base64.StdEncoding.DecodeString(secretStr)
	}
	if err != nil {
		decoded = raw
	}
	if len(decoded) < 32 {
		return nil, fmt.Errorf("decoded seed too short: %d bytes (need 32)", len(decoded))
	}
	return decoded[:32], nil
}


func (mc *MatrixClient) ImportKeysFromBeeperDesktop(ctx context.Context) (int, error) {
	mc.mu.RLock()
	client := mc.client
	cryptoH := mc.crypto
	mc.mu.RUnlock()

	if client == nil || cryptoH == nil {
		return 0, fmt.Errorf("not logged in")
	}

	machine := cryptoH.Machine()
	if machine == nil {
		return 0, fmt.Errorf("crypto not initialized")
	}

	appData := os.Getenv("APPDATA")
	if appData == "" {
		home, _ := os.UserHomeDir()
		appData = filepath.Join(home, "AppData", "Roaming")
	}

	dbPaths := []string{
		filepath.Join(appData, "BeeperTexts", "account.db"),
		filepath.Join(appData, "Beeper", "account.db"),
		filepath.Join(appData, "Beeper Nightly", "account.db"),
	}

	var dbPath string
	for _, p := range dbPaths {
		if _, err := os.Stat(p); err == nil {
			dbPath = p
			break
		}
	}
	if dbPath == "" {
		return 0, fmt.Errorf("Beeper Desktop database not found (checked BeeperTexts, Beeper, Beeper Nightly in %%APPDATA%%)")
	}

	log.Info().Str("path", dbPath).Msg("Found Beeper Desktop database")

	db, err := sql.Open("sqlite3", dbPath+"?mode=ro")
	if err != nil {
		return 0, fmt.Errorf("opening Beeper Desktop database: %w", err)
	}
	defer db.Close()

	secrets := map[string][]byte{}
	rows, err := db.QueryContext(ctx, "SELECT name, secret FROM crypto_secrets")
	if err != nil {
		return 0, fmt.Errorf("reading crypto_secrets: %w", err)
	}
	defer rows.Close()

	for rows.Next() {
		var name string
		var secret []byte
		if err := rows.Scan(&name, &secret); err != nil {
			continue
		}
		secrets[name] = secret
		log.Info().Str("name", name).Int("len", len(secret)).Msg("Found crypto secret")
	}

	if len(secrets) == 0 {
		return 0, fmt.Errorf("no crypto secrets found in Beeper Desktop database")
	}

	imported := 0

	masterRaw, hasMaster := secrets["m.cross_signing.master"]
	selfSignRaw, hasSelfSign := secrets["m.cross_signing.self_signing"]
	userSignRaw, hasUserSign := secrets["m.cross_signing.user_signing"]

	if hasMaster && hasSelfSign && hasUserSign {
		masterSeed, err1 := extractSeed32(masterRaw)
		selfSignSeed, err2 := extractSeed32(selfSignRaw)
		userSignSeed, err3 := extractSeed32(userSignRaw)

		if err1 == nil && err2 == nil && err3 == nil {
			log.Info().Msg("Importing cross-signing keys from Beeper Desktop...")

			err := machine.ImportCrossSigningKeys(crypto.CrossSigningSeeds{
				MasterKey:      masterSeed,
				SelfSigningKey: selfSignSeed,
				UserSigningKey: userSignSeed,
			})
			if err != nil {
				log.Error().Err(err).Msg("Failed to import cross-signing keys")
			} else {
				log.Info().Msg("Cross-signing keys imported successfully")
				imported++

				if err := machine.SignOwnMasterKey(ctx); err != nil {
					log.Warn().Err(err).Msg("Failed to sign own master key (may already be signed)")
				} else {
					log.Info().Msg("Signed own master key")
				}

				deviceID := string(client.DeviceID)

				if deviceID != "" {
					myDevice, err := machine.CryptoStore.GetDevice(ctx, client.UserID, id.DeviceID(deviceID))
					if err == nil && myDevice != nil {
						if err := machine.SignOwnDevice(ctx, myDevice); err != nil {
							log.Warn().Err(err).Msg("Failed to sign own device")
						} else {
							log.Info().Str("deviceID", deviceID).Msg("Signed own device — now trusted by cross-signing")
							imported++
						}
					} else {
						log.Warn().Str("deviceID", deviceID).Msg("Could not find own device in crypto store")
					}
				}
			}
		} else {
			log.Warn().Msg("Failed to extract cross-signing seeds from Beeper Desktop secrets")
		}
	} else {
		log.Warn().Bool("master", hasMaster).Bool("selfSign", hasSelfSign).Bool("userSign", hasUserSign).
			Msg("Not all cross-signing keys found in Beeper Desktop database")
	}

	backupKeyRaw, hasBackup := secrets["m.megolm_backup.v1"]
	if hasBackup {
		backupSeed, err := extractSeed32(backupKeyRaw)
		if err != nil {
			log.Warn().Err(err).Msg("Failed to extract backup key seed")
		} else {
			log.Info().Msg("Importing megolm backup key...")
			megolmBackupKey, err := backup.MegolmBackupKeyFromBytes(backupSeed)
			if err != nil {
				log.Warn().Err(err).Msg("Failed to create backup key from seed")
			} else {
				version, err := machine.DownloadAndStoreLatestKeyBackup(ctx, megolmBackupKey)
				if err != nil {
					log.Warn().Err(err).Msg("Failed to download key backup (keys may be wrong)")
				} else {
					log.Info().Str("version", string(version)).Msg("Key backup downloaded")
					imported++
				}
			}
		}
	}

	for _, name := range []string{"m.cross_signing.master", "m.cross_signing.self_signing", "m.cross_signing.user_signing", "m.megolm_backup.v1"} {
		if raw, ok := secrets[name]; ok {
			seed, err := extractSeed32(raw)
			if err == nil {
				machine.CryptoStore.PutSecret(ctx, id.Secret(name), base64.RawStdEncoding.EncodeToString(seed))
			}
		}
	}

	if imported == 0 {
		return 0, fmt.Errorf("no keys could be imported — cross-signing secrets may be corrupted")
	}

	return imported, nil
}
