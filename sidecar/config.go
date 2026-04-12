package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
)

type Config struct {
	mu       sync.RWMutex
	dataDir  string
	filePath string

	Session  SessionConfig `json:"session"`
	Settings AppSettings   `json:"settings"`
}

type SessionConfig struct {
	HomeserverURL string `json:"homeserver_url"`
	AccessToken   string `json:"access_token"`
	UserID        string `json:"user_id"`
	DeviceID      string `json:"device_id"`
	SyncToken     string `json:"sync_token"`
	LocalAPIToken string `json:"local_api_token"`
}

type AppSettings struct {
	Port int `json:"port,omitempty"`
}

func NewConfig(dataDir string) *Config {
	return &Config{
		dataDir:  dataDir,
		filePath: filepath.Join(dataDir, "config.json"),
	}
}

func (c *Config) DataDir() string {
	return c.dataDir
}

func (c *Config) CryptoDBPath() string {
	return filepath.Join(c.dataDir, "crypto.db")
}

func (c *Config) HasSession() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.Session.AccessToken != "" && c.Session.UserID != ""
}

func (c *Config) GetLocalAPIToken() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.Session.LocalAPIToken == "" {
		c.Session.LocalAPIToken = generateToken()
		c.saveUnsafe()
	}
	return c.Session.LocalAPIToken
}

func (c *Config) SaveSession(homeserver, accessToken, userID, deviceID string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.Session.HomeserverURL = homeserver
	c.Session.AccessToken = accessToken
	c.Session.UserID = userID
	c.Session.DeviceID = deviceID
	if c.Session.LocalAPIToken == "" {
		c.Session.LocalAPIToken = generateToken()
	}
	return c.saveUnsafe()
}

func (c *Config) SaveSyncToken(token string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.Session.SyncToken = token
	c.saveUnsafe()
}

func (c *Config) ClearSession() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.Session = SessionConfig{}
	return c.saveUnsafe()
}

func (c *Config) Load() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	data, err := os.ReadFile(c.filePath)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, c)
}

func (c *Config) saveUnsafe() error {
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(c.filePath, data, 0600)
}

func generateToken() string {
	b := make([]byte, 32)
	rand.Read(b)
	return hex.EncodeToString(b)
}
