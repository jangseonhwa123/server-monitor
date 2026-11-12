using Xunit;

namespace Server.Tests;

// 모든 테스트 클래스가 서버 인스턴스 하나를 공유합니다 (DB와 쓰기 큐가 프로세스 단위로 하나이기 때문)
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory> { }
