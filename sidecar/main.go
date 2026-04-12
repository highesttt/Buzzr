package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"syscall"

	"github.com/rs/zerolog"
	"github.com/rs/zerolog/log"
)

func defaultDataDir() string {
	if runtime.GOOS == "windows" {
		return filepath.Join(os.Getenv("LOCALAPPDATA"), "Buzzr")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".config", "buzzr")
}

func main() {
	port := flag.Int("port", 29110, "HTTP server port")
	dataDir := flag.String("data", defaultDataDir(), "Data directory for session and crypto storage")
	debug := flag.Bool("debug", false, "Enable debug logging")
	flag.Parse()

	zerolog.TimeFieldFormat = zerolog.TimeFormatUnix
	consoleWriter := zerolog.ConsoleWriter{Out: os.Stdout}
	log.Logger = zerolog.New(consoleWriter).With().Timestamp().Logger()
	if *debug {
		zerolog.SetGlobalLevel(zerolog.DebugLevel)
	} else {
		zerolog.SetGlobalLevel(zerolog.InfoLevel)
	}

	log.Info().
		Int("port", *port).
		Str("dataDir", *dataDir).
		Msg("Starting Buzzr sidecar")

	if err := os.MkdirAll(*dataDir, 0700); err != nil {
		log.Fatal().Err(err).Msg("Failed to create data directory")
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	cfg := NewConfig(*dataDir)
	if err := cfg.Load(); err != nil {
		log.Warn().Err(err).Msg("No existing config, starting fresh")
	}

	store := NewStore()
	if err := store.InitDB(*dataDir); err != nil {
		log.Warn().Err(err).Msg("Failed to init SQLite store, running without persistence")
	} else {
		log.Info().Int("rooms", store.RoomCount()).Msg("SQLite store loaded")
	}
	wsHub := NewWSHub()
	cfg.Settings.Port = *port
	mc := NewMatrixClient(cfg, store, wsHub)
	srv := NewServer(*port, mc, store, wsHub, cfg)

	if cfg.HasSession() {
		log.Info().Msg("Found saved session, attempting to resume...")
		if err := mc.Resume(ctx); err != nil {
			log.Error().Err(err).Msg("Failed to resume session (re-login required)")
		} else {
			log.Info().Str("userID", cfg.Session.UserID).Msg("Session resumed successfully")
			go mc.StartSync(ctx)
		}
	} else {
		log.Info().Msg("No saved session. Use POST /v1/auth/login to authenticate.")
	}

	go func() {
		if err := srv.Start(); err != nil {
			log.Fatal().Err(err).Msg("HTTP server failed")
		}
	}()

	fmt.Printf("\n  Buzzr sidecar running on http://localhost:%d\n\n", *port)

	quit := make(chan os.Signal, 1)
	signal.Notify(quit, os.Interrupt, syscall.SIGTERM)
	<-quit

	log.Info().Msg("Shutting down...")
	cancel()
	mc.Stop()
	srv.Stop()
	store.CloseDB()
	log.Info().Msg("Goodbye!")
}
