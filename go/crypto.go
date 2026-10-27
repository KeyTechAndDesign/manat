package manat

import (
	"crypto"
	"crypto/md5"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/x509"
	"encoding/hex"
	"encoding/pem"
	"errors"
)

func md5Hex(s string) string {
	sum := md5.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

func safeEqual(a, b string) bool {
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}

// rsaPrivateKey reads PKCS#1 ("RSA PRIVATE KEY") or PKCS#8 ("PRIVATE KEY") PEM.
func rsaPrivateKey(text string) (*rsa.PrivateKey, error) {
	block, _ := pem.Decode([]byte(text))
	if block == nil {
		return nil, errors.New("not a PEM key")
	}
	switch block.Type {
	case "RSA PRIVATE KEY":
		return x509.ParsePKCS1PrivateKey(block.Bytes)
	case "PRIVATE KEY":
		key, err := x509.ParsePKCS8PrivateKey(block.Bytes)
		if err != nil {
			return nil, err
		}
		if rsaKey, ok := key.(*rsa.PrivateKey); ok {
			return rsaKey, nil
		}
		return nil, errors.New("not an RSA key")
	}
	return nil, errors.New("unsupported PEM block " + block.Type + " (encrypted keys are not supported)")
}

// rsaPublicKey reads SPKI ("PUBLIC KEY"), PKCS#1 ("RSA PUBLIC KEY") or a certificate.
func rsaPublicKey(text string) (*rsa.PublicKey, error) {
	block, _ := pem.Decode([]byte(text))
	if block == nil {
		return nil, errors.New("not a PEM key")
	}
	switch block.Type {
	case "RSA PUBLIC KEY":
		return x509.ParsePKCS1PublicKey(block.Bytes)
	case "PUBLIC KEY":
		key, err := x509.ParsePKIXPublicKey(block.Bytes)
		if err != nil {
			return nil, err
		}
		if rsaKey, ok := key.(*rsa.PublicKey); ok {
			return rsaKey, nil
		}
		return nil, errors.New("not an RSA key")
	case "CERTIFICATE":
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, err
		}
		if rsaKey, ok := cert.PublicKey.(*rsa.PublicKey); ok {
			return rsaKey, nil
		}
		return nil, errors.New("certificate has no RSA key")
	}
	return nil, errors.New("unsupported PEM block " + block.Type)
}

// signSHA256 is RSA-SHA256 (PKCS#1 v1.5) over the text.
func signSHA256(key *rsa.PrivateKey, text string) ([]byte, error) {
	sum := sha256.Sum256([]byte(text))
	return rsa.SignPKCS1v15(nil, key, crypto.SHA256, sum[:])
}

func verifySHA256(key *rsa.PublicKey, text string, signature []byte) bool {
	sum := sha256.Sum256([]byte(text))
	return rsa.VerifyPKCS1v15(key, crypto.SHA256, sum[:], signature) == nil
}
