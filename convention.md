# 참고할 git convention

## 기본 커밋 타입

* `feat`: 새로운 기능 추가
* `fix`: 버그 수정
* `docs`: 문서 수정
* `style`: 코드 스타일 변경 (포맷팅, 세미콜론 등)
* `design`: UI 디자인 변경
* `test`: 테스트 코드 작업
* `refactor`: 기능 변화 없는 코드 개선
* `build`: 빌드 관련 수정
* `ci`: CI/CD 설정 수정
* `perf`: 성능 개선
* `chore`: 자잘한 수정, 의존성 업데이트
* `rename`: 파일/폴더 이름 변경
* `remove`: 파일 삭제

---

# 자주 같이 쓰는 규칙

## 1. 커밋 메시지는 현재형으로 작성

좋은 예:

```bash
feat: add login api
fix: resolve memory leak
```

별로인 예:

```bash
feat: added login api
fix: fixed bug
```

보통 “무엇을 했다”보다
“무엇을 추가/수정한다” 느낌으로 씀.

---

## 2. 제목은 짧고 명확하게

권장:

```bash
fix: null pointer on startup
```

지양:

```bash
fix: when user click button application crashes because...
```

한 줄 요약 느낌으로.

---

## 3. 타입 뒤에는 콜론 + 공백

```bash
feat: add dark mode
```

아래처럼 붙여쓰지 않음:

```bash
feat:add dark mode
```

---

## 4. 가능하면 한 커밋 = 한 작업

좋음:

```bash
feat: add jwt authentication
fix: resolve token refresh bug
```

안 좋음:

```bash
feat: add auth + change ui + fix docker + update docs
```

나중에 롤백/추적이 힘들어짐.

---

## 5. refactor와 fix 차이

### `fix`

실제 버그 수정

```bash
fix: prevent crash when config is missing
```

### `refactor`

동작 변화 없이 구조 개선

```bash
refactor: simplify websocket handler
```

---

## 6. style vs design 차이

### `style`

코드 스타일

```bash
style: format code with prettier
```

### `design`

실제 UI 디자인 변경

```bash
design: update dashboard layout
```

---

## 7. chore는 “기타 잡일”

대표 예시:

```bash
chore: update dependencies
chore: cleanup unused imports
```

애매하면 일단 chore 쓰는 경우 많음.

---

# 많이 쓰는 추가 패턴

## scope 사용

형식:

```bash
type(scope): message
```

예시:

```bash
feat(auth): add oauth login
fix(api): handle timeout error
refactor(db): simplify query builder
```

프로젝트 커질수록 매우 유용함.

---

# 브랜치 네이밍 예시

보통 이런 식 사용:

```bash
feature/login-api
fix/docker-build
refactor/auth-module
hotfix/payment-error
```

또는:

```bash
feat/login
fix/crash-on-startup
```

---

# 작업 흐름 추천

```bash
main/master
 └─ develop
     ├─ feature/*
     ├─ fix/*
     └─ hotfix/*
```

개인 프로젝트면 너무 복잡하게 갈 필요는 없음.

간단히:

```bash
main
 ├─ feat/*
 └─ fix/*
```

정도만 해도 충분히 깔끔함.

---

# PR 제목도 비슷하게 맞추면 좋음

```bash
feat: add realtime chat
fix: resolve websocket reconnect issue
```

커밋 규칙과 통일하면 히스토리가 보기 좋아짐.

---

# 실무에서 꽤 중요한 습관

## 1. WIP 남발하지 않기

```bash
wip
asdf
test
123
```

이런 커밋은 나중에 지옥 된다.

---

## 2. 커밋은 “의미 단위”로 자르기

좋은 커밋:

* 로그인 기능 추가
* 토큰 갱신 수정
* DB 쿼리 최적화

안 좋은 커밋:

* 오늘 작업한 거 전부

---

## 3. push 전에 커밋 정리하기

자잘한 커밋은 squash 자주 함.

```bash
git rebase -i HEAD~3
```

실무에서도 매우 많이 씀.

---

# 추천 Conventional Commit 형태

```bash
feat(auth): add jwt login
fix(api): prevent duplicate request
refactor(core): split parser module
docs(readme): update installation guide
```

이 정도만 지켜도 협업 퀄리티가 확 올라감.
